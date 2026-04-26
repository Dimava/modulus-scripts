using ScriptEngine;

/// <summary>
/// Legacy entry point kept so old config entries have an obvious migration target. The actual right-click drag fix lives in
/// <c>ToolbarMouseShortcutDragGuard.cs</c>; this script no longer installs diagnostic patches or logs.
/// </summary>
[ScriptEntry]
public sealed class NoopRightClickDragCompatibilityShim : ScriptMod
{
}
