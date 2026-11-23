using System.Windows.Input;

namespace RobloxAfkManager.App.ViewModels;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly Predicate<object?> canExecute;
    private readonly Action<Exception> onError;
    public AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onError)
        : this(_ => execute(), _ => canExecute(), onError) { }
    public AsyncCommand(Func<object?, Task> execute, Predicate<object?> canExecute, Action<Exception> onError)
    { this.execute = execute; this.canExecute = canExecute; this.onError = onError; }
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && canExecute(parameter);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; Notify();
        try { await execute(parameter); }
        catch (Exception ex) { onError(ex); }
        finally { running = false; Notify(); }
    }
    public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
