#include <windows.h>
#include <dwmapi.h>
#include <roapi.h>
#include <d3d11.h>
#include <dxgi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <algorithm>
#include <memory>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

namespace {
struct HandleCloser { void operator()(void* value) const { if (value) CloseHandle(value); } };
using unique_handle = std::unique_ptr<void, HandleCloser>;
bool matches(HWND hwnd, DWORD pid, unsigned long long started) {
    DWORD owner = 0;
    if (!IsWindow(hwnd) || !GetWindowThreadProcessId(hwnd, &owner) || owner != pid) return false;
    unique_handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    FILETIME creation{}, exit{}, kernel{}, user{};
    if (!process || !GetProcessTimes(process.get(), &creation, &exit, &kernel, &user)) return false;
    ULARGE_INTEGER time{}; time.LowPart = creation.dwLowDateTime; time.HighPart = creation.dwHighDateTime;
    wchar_t path[32768]; DWORD length = ARRAYSIZE(path);
    if (!QueryFullProcessImageNameW(process.get(), 0, path, &length)) return false;
    auto name = wcsrchr(path, L'\\'); name = name ? name + 1 : path;
    return time.QuadPart == started && _wcsicmp(name, L"RobloxPlayerBeta.exe") == 0;
}
struct Apartment { HRESULT hr = RoInitialize(RO_INIT_MULTITHREADED); ~Apartment() { if (SUCCEEDED(hr)) RoUninitialize(); } };
struct CaptureScope {
    GraphicsCaptureSession session{nullptr}; Direct3D11CaptureFramePool pool{nullptr};
    ~CaptureScope() { try { if (session) session.Close(); } catch (...) {} try { if (pool) pool.Close(); } catch (...) {} }
};
}

// A bounded one-shot capture. Never changes visibility, placement, foreground, or game state.
extern "C" __declspec(dllexport) HRESULT __cdecl CaptureWindow(
    HWND hwnd, DWORD pid, unsigned long long started, unsigned char* pixels,
    int capacity, int maxWidth, int maxHeight, int* width, int* height) noexcept {
    try {
        if (!pixels || !width || !height || maxWidth < 1 || maxWidth > 960 || maxHeight < 1 || maxHeight > 540 || capacity < maxWidth * maxHeight * 4)
            return E_INVALIDARG;
        *width = *height = 0;
        if (!matches(hwnd, pid, started)) return HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE);
        DWORD cloaked = 0; DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked));
        if (IsIconic(hwnd) || cloaked || IsHungAppWindow(hwnd)) return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        Apartment apartment; check_hresult(apartment.hr);
        if (!GraphicsCaptureSession::IsSupported()) return E_NOTIMPL;
        com_ptr<ID3D11Device> device; com_ptr<ID3D11DeviceContext> context;
        check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, device.put(), nullptr, context.put()));
        auto dxgi = device.as<IDXGIDevice>(); com_ptr<IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
        auto direct3d = inspectable.as<IDirect3DDevice>();
        auto factory = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        GraphicsCaptureItem item{nullptr};
        check_hresult(factory->CreateForWindow(hwnd, guid_of<GraphicsCaptureItem>(), put_abi(item)));
        const auto size = item.Size();
        // Prevent pathological allocations; one source texture and one staging texture at a time.
        if (size.Width <= 0 || size.Height <= 0 || size.Width > 8192 || size.Height > 8192 || static_cast<long long>(size.Width) * size.Height > 16777216) return E_INVALIDARG;
        CaptureScope scope;
        scope.pool = Direct3D11CaptureFramePool::CreateFreeThreaded(direct3d, DirectXPixelFormat::B8G8R8A8UIntNormalized, 1, size);
        scope.session = scope.pool.CreateCaptureSession(item);
        if (auto session2 = scope.session.try_as<IGraphicsCaptureSession2>()) session2.IsCursorCaptureEnabled(false);
        scope.session.StartCapture();
        Direct3D11CaptureFrame frame{nullptr};
        const auto deadline = GetTickCount64() + 800;
        while (!(frame = scope.pool.TryGetNextFrame()) && GetTickCount64() < deadline) Sleep(10);
        if (!frame) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        com_ptr<ID3D11Texture2D> texture; check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), texture.put_void()));
        D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
        const auto content = frame.ContentSize();
        if (content.Width != size.Width || content.Height != size.Height) return HRESULT_FROM_WIN32(ERROR_RETRY);
        desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.MiscFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        com_ptr<ID3D11Texture2D> staging; check_hresult(device->CreateTexture2D(&desc, nullptr, staging.put()));
        context->CopyResource(staging.get(), texture.get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        check_hresult(context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped));
        double scale = std::min({1.0, static_cast<double>(maxWidth) / size.Width, static_cast<double>(maxHeight) / size.Height});
        *width = std::max(1, static_cast<int>(size.Width * scale));
        *height = std::max(1, static_cast<int>(size.Height * scale));
        for (int y = 0; y < *height; ++y) {
            auto source = static_cast<const unsigned char*>(mapped.pData) + static_cast<size_t>(y * size.Height / *height) * mapped.RowPitch;
            auto target = pixels + static_cast<size_t>(y) * *width * 4;
            for (int x = 0; x < *width; ++x) memcpy(target + x * 4, source + static_cast<size_t>(x * size.Width / *width) * 4, 4);
        }
        context->Unmap(staging.get(), 0);
        frame.Close();
        if (!matches(hwnd, pid, started)) { *width = *height = 0; return HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE); }
        return S_OK;
    } catch (...) { return to_hresult(); }
}
