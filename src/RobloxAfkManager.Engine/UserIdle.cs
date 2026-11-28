namespace RobloxAfkManager.Engine;

// How long the person has not touched the keyboard or mouse. Used to tell "at the PC" from "away".
public interface IUserIdle { TimeSpan IdleFor(); }

public sealed class NeverIdle : IUserIdle { public TimeSpan IdleFor() => TimeSpan.Zero; }
