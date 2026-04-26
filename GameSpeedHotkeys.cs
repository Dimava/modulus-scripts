using System.Reflection;
using Data.GameState;
using Data.Variables;
using ScriptEngine;
using StompyRobot.SROptions;
using UnityEngine;

/// <summary>
/// Press [ to slow down, ] to speed up, and \ to reset to normal speed.
/// Available speeds are 1x, 2x, and 4x.
/// </summary>
[ScriptEntry]
public sealed class GameSpeedHotkeys : ScriptMod
{
    private static readonly float[] Speeds = { 1f, 2f, 4f };
    private static readonly FieldInfo PauseStoredMultiplierField = typeof(PauseStateData).GetField("_currentGlobalUpdateMultiplier", BindingFlags.Instance | BindingFlags.NonPublic);

    private ScriptConfigEntry<bool> _useUnityTimeScale;
    private int _speedIndex;
    private float _baseFixedDeltaTime;

    protected override void OnEnable()
    {
        BindKey("keySlower", "LeftBracket");
        BindKey("keyFaster", "RightBracket");
        BindKey("keyReset", "Backslash");
        _useUnityTimeScale = BindBool("UseUnityTimeScale", false);

        _baseFixedDeltaTime = Time.fixedDeltaTime / Mathf.Max(Time.timeScale, 0.0001f);
        _speedIndex = ClosestSpeedIndex(GetCurrentSpeed());
        ApplySpeed(_speedIndex, false);
    }

    protected override void OnDisable()
    {
        ApplyGameMultiplier(1);
        Time.timeScale = 1f;
        if (_baseFixedDeltaTime > 0f)
        {
            Time.fixedDeltaTime = _baseFixedDeltaTime;
        }
    }

    protected override void OnConfigChanged()
    {
        ApplyGameMultiplier(1);
        Time.timeScale = 1f;
        if (_baseFixedDeltaTime > 0f)
        {
            Time.fixedDeltaTime = _baseFixedDeltaTime;
        }

        ApplySpeed(_speedIndex, true);
    }

    protected override void OnUpdate()
    {
        if (WasPressed("keySlower"))
        {
            ApplySpeed(Mathf.Max(0, _speedIndex - 1), true);
            return;
        }

        if (WasPressed("keyFaster"))
        {
            ApplySpeed(Mathf.Min(Speeds.Length - 1, _speedIndex + 1), true);
            return;
        }

        if (WasPressed("keyReset"))
        {
            ApplySpeed(0, true);
        }
    }

    private void ApplySpeed(int speedIndex, bool announce)
    {
        _speedIndex = speedIndex;

        float speed = Speeds[_speedIndex];
        if (_useUnityTimeScale.Value)
        {
            Time.timeScale = speed;
            Time.fixedDeltaTime = _baseFixedDeltaTime * speed;
        }
        else
        {
            Time.timeScale = 1f;
            if (_baseFixedDeltaTime > 0f)
            {
                Time.fixedDeltaTime = _baseFixedDeltaTime;
            }

            ApplyGameMultiplier((int)speed);
        }

        if (announce)
        {
            Log($"Time scale: {speed:0}x ({(_useUnityTimeScale.Value ? "Unity Time.timeScale" : "game update multiplier")})");
        }
    }

    private float GetCurrentSpeed()
    {
        if (_useUnityTimeScale != null && _useUnityTimeScale.Value)
        {
            return Time.timeScale;
        }

        return GetDisplayedGameMultiplier();
    }

    private static int GetDisplayedGameMultiplier()
    {
        IntVariableSO multiplier;
        if (!TryGetGameMultiplier(out multiplier) || multiplier == null)
        {
            return 1;
        }

        if (multiplier.Value > 0)
        {
            return multiplier.Value;
        }

        if (PauseStoredMultiplierField != null)
        {
            foreach (PauseStateData pause in Resources.FindObjectsOfTypeAll<PauseStateData>())
            {
                if (pause == null)
                {
                    continue;
                }

                object storedValue = PauseStoredMultiplierField.GetValue(pause);
                if (storedValue is int && (int)storedValue > 0)
                {
                    return (int)storedValue;
                }
            }
        }

        return 1;
    }

    private static void ApplyGameMultiplier(int speed)
    {
        if (speed != 1 && speed != 2 && speed != 4)
        {
            speed = 1;
        }

        IntVariableSO multiplier;
        if (!TryGetGameMultiplier(out multiplier) || multiplier == null)
        {
            return;
        }

        if (multiplier.Value == 0)
        {
            if (PauseStoredMultiplierField == null)
            {
                return;
            }

            foreach (PauseStateData pause in Resources.FindObjectsOfTypeAll<PauseStateData>())
            {
                if (pause != null)
                {
                    PauseStoredMultiplierField.SetValue(pause, speed);
                }
            }
            return;
        }

        if (multiplier.Value != speed)
        {
            multiplier.SetValue(speed);
        }
    }

    private static bool TryGetGameMultiplier(out IntVariableSO multiplier)
    {
        multiplier = null;

        SROptionsReferences references = SROptionsReferences.Instance;
        if (references == null)
        {
            return false;
        }

        multiplier = references.GlobalUpdateMultiplier;
        return multiplier != null;
    }

    private static int ClosestSpeedIndex(float speed)
    {
        int bestIndex = 0;
        float bestDistance = Mathf.Abs(speed - Speeds[0]);

        for (int i = 1; i < Speeds.Length; i++)
        {
            float distance = Mathf.Abs(speed - Speeds[i]);
            if (distance < bestDistance)
            {
                bestIndex = i;
                bestDistance = distance;
            }
        }

        return bestIndex;
    }
}
