using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Network;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using StanleyParableXiv.Services;

namespace StanleyParableXiv.Events;

public class DebugEvent : IDisposable
{
    [Signature("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 83 EC ?? 33 FF 48 8B D9 41 0F B7 08", DetourName = nameof(ContentDirectorNetworkMessageDetour), UseFlags = SignatureUseFlags.Hook)]
    private readonly Hook<SetupContentDirectNetworkMessageDelegate>? _contentDirectorNetworkMessageHook = null;

    private readonly Dictionary<ConditionFlag, bool> _conditions = new();

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate byte SetupContentDirectNetworkMessageDelegate(IntPtr a1, IntPtr a2, ushort* a3);

    /// <summary>
    /// Fires on login events.
    /// </summary>
    public DebugEvent()
    {
        DalamudService.Framework.Update += OnFrameworkUpdate;
         DalamudService.Framework.RunOnFrameworkThread(() =>
         {
            DalamudService.GameInteropProvider.InitializeFromAttributes(this);
            _contentDirectorNetworkMessageHook?.Enable();
         });
    }

    public void Dispose()
    {
        DalamudService.Framework.Update -= OnFrameworkUpdate;
         _contentDirectorNetworkMessageHook?.Dispose();

        GC.SuppressFinalize(this);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Configuration.Instance.EnableDebugLogging) return;
        LogConditionFlagChanges();
    }

    private unsafe byte ContentDirectorNetworkMessageDetour(IntPtr a1, IntPtr a2, ushort* a3)
    {
        try
        {
            if (Configuration.Instance.EnableDebugLogging)
            {

                var category = *a3;
                var type = *(uint*)(a3 + 4);

                DalamudService.Log.Verbose("Cat = 0x{Cat:X}, UpdateType = 0x{UpdateType:X}",
                    category, type);
            }
        }
        catch (Exception e)
        {
            DalamudService.Log.Error(e, "Error in PvpEvent.ContentDirectorNetworkMessageDetour");
        }

        return this._contentDirectorNetworkMessageHook!.Original(a1, a2, a3);
    }

    private void LogConditionFlagChanges()
    {
        foreach (int condition in Enum.GetValues(typeof(ConditionFlag)))
        {
            ConditionFlag flag = (ConditionFlag)condition;
            bool currentValue = DalamudService.Condition[flag];

            if (_conditions.ContainsKey(flag) && _conditions[flag] != currentValue)
            {
                DalamudService.Log.Verbose("Condition for {Flag} changed: {LastValue} -> {CurrentValue}", flag, _conditions[flag], currentValue);
            }

            _conditions[flag] = currentValue;
        }
    }
}