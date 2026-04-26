using System;
using HarmonyLib;
using ScriptEngine;
using UnityEngine;
disabled
[ScriptEntry]
public sealed class LoadingScreenExceptionLogger : ScriptMod
{
    private static LoadingScreenExceptionLogger? _instance;

    protected override void OnEnable()
    {
        _instance = this;
        Log("LoadingScreenExceptionLogger enabled: logging ExceptionHandler popup exceptions to this script log.");
    }

    protected override void OnDisable()
    {
        Log("LoadingScreenExceptionLogger disabled.");

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    public static void LogPopupException(ExceptionHandler exceptionHandler, string condition, string stackTrace, LogType type)
    {
        if (_instance == null || exceptionHandler == null)
        {
            return;
        }

        bool loadingScreenIsOpen = Traverse.Create(exceptionHandler).Field("_loadingScreenIsOpen").GetValue<bool>();
        bool isRecovering = Traverse.Create(exceptionHandler).Field("_isRecovering").GetValue<bool>();
        if ((type != LogType.Exception && type != LogType.Assert) || !loadingScreenIsOpen || isRecovering)
        {
            return;
        }

        _instance.Error(
            "Captured ExceptionHandler popup exception\n" +
            $"Type: {type}\n" +
            $"Condition: {condition}\n\n" +
            "StackTrace:\n" +
            (string.IsNullOrWhiteSpace(stackTrace) ? "<empty>" : stackTrace));
    }
}

[HarmonyPatch(typeof(ExceptionHandler), "OnLog")]
static class LoadingScreenExceptionLogger_ExceptionHandler_OnLog_Patch
{
    static void Prefix(ExceptionHandler __instance, string condition, string stackTrace, LogType type)
    {
        LoadingScreenExceptionLogger.LogPopupException(__instance, condition, stackTrace, type);
    }
}
