using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Network;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using Lumina.Excel.Sheets;
using StanleyParableXiv.Services;
using StanleyParableXiv.Utility;

namespace StanleyParableXiv.Events;

public class PvpEvent : IDisposable
{
    private bool _firstBlood = false;

    private Dictionary<string, uint> _killStreaks = new();
    private Dictionary<string, uint> _multikills = new();
    private Dictionary<string, DateTimeOffset> _multikillCooldowns = new();

    [Signature("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 83 EC ?? 33 FF 48 8B D9 41 0F B7 08", DetourName = nameof(ContentDirectorNetworkMessageDetour), UseFlags = SignatureUseFlags.Hook)]
    private readonly Hook<SetupContentDirectNetworkMessageDelegate>? _contentDirectorNetworkMessageHook = null;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate byte SetupContentDirectNetworkMessageDelegate(IntPtr a1, IntPtr a2, ushort* a3);

    private readonly uint?[] _frontlineTerritoryIds =
    [
        376, // Borderland Ruins
        431, // Seal Rock
        554, // The Fields of Glory
        888, // Onsal Hakair
        1313, // Worqor Chirteh
    ];

    private readonly uint?[] _rivalWingsTerritoryIds =
    [
        729, // Astragalos
        791, // Hidden Gorge
    ];

    /// <summary>
    /// Fires on specific PvP related events.
    /// </summary>
    public PvpEvent()
    {
        DalamudService.ClientState.EnterPvP += OnEnterPvP;
        DalamudService.ClientState.LeavePvP += OnLeavePvp;
        DalamudService.ChatGui.ChatMessage += OnChatMessage;
        DalamudService.Framework.RunOnFrameworkThread(() =>
        {
            DalamudService.GameInteropProvider.InitializeFromAttributes(this);
            _contentDirectorNetworkMessageHook?.Enable();
        });
        TerritoryService.Instance.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        DalamudService.ClientState.EnterPvP -= OnEnterPvP;
        DalamudService.ClientState.LeavePvP -= OnLeavePvp;
        DalamudService.ChatGui.ChatMessage -= OnChatMessage;
        _contentDirectorNetworkMessageHook?.Dispose();
        TerritoryService.Instance.TerritoryChanged -= OnTerritoryChanged;

        GC.SuppressFinalize(this);
    }

    private static void OnEnterPvP()
    {
        if (TerritoryService.Instance.CurrentTerritory == null) return;
        if (!DalamudService.ClientState.IsPvPExcludingDen) return;

        DalamudService.Log.Debug("Entering PvP");

        if (Configuration.Instance.EnablePvpPrepareEvent)
        {
            Task.Delay(5000).ContinueWith(_ =>
            {
                AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.PvpPrepare);
            });
        }
    }

    private void OnLeavePvp()
    {
        DalamudService.Log.Debug("Leaving PvP");
        ResetPvp();
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (!DalamudService.ClientState.IsPvPExcludingDen) return;
        if (type is not ((XivChatType)4922 or (XivChatType)2874)) return;
        DalamudService.Log.Verbose("[{Type}] {Message}", type, message);

        PlayerPayload?[] playerPayloads = message.Payloads
            .Where(x => x.Type == PayloadType.Player)
            .Select(x => x as PlayerPayload)
            .ToArray();

        if (playerPayloads.Length == 0) return;

        DateTimeOffset killTime = DateTimeOffset.Now;
        bool chatLogKillStreaks = Configuration.Instance.EnablePvpChatEvent;

        string? killerName = null;
        string? killedName = null;

        switch (type)
        {
            case (XivChatType)4922 when playerPayloads.Length == 2:
            {
                PlayerPayload? player1 = playerPayloads[0];
                PlayerPayload? player2 = playerPayloads[1];

                if (player1 == null || player2 == null) return;

                string? player1Name = XivUtility.GetFullPlayerName(player1);
                string? player2Name = XivUtility.GetFullPlayerName(player2);

                if (IsInParty(player1))
                {
                    if (IsInPartyAndProbablyDead(player1))
                    {
                        killerName = player2Name;
                        killedName = player1Name;
                    }
                    else
                    {
                        killerName = player1Name;
                        killedName = player2Name;
                    }
                }
                else if (IsInParty(player2))
                {
                    if (IsInPartyAndProbablyDead(player2))
                    {
                        killerName = player1Name;
                        killedName = player2Name;
                    }
                    else
                    {
                        killerName = player2Name;
                        killedName = player1Name;
                    }
                }

                break;
            }
            case (XivChatType)2874 when playerPayloads.Length == 1:
            {
                PlayerPayload? otherPlayer = playerPayloads[0];
                IPlayerCharacter? localPlayer = DalamudService.ObjectTable.LocalPlayer;

                if (otherPlayer == null || localPlayer == null) return;

                string? otherPlayerName = XivUtility.GetFullPlayerName(otherPlayer);
                string? localPlayerName = XivUtility.GetFullPlayerName(localPlayer);

                // Determine who killed who depending on if you died or not.
                if (localPlayer.IsDead)
                {
                    killerName = otherPlayerName;
                    killedName = localPlayerName;
                }
                else
                {
                    killerName = localPlayerName;
                    killedName = otherPlayerName;
                }

                break;
            }
            default:
                return;
        }

        if (string.IsNullOrEmpty(killerName) || string.IsNullOrEmpty(killedName)) return;

        DalamudService.Log.Verbose("{KillerName} -> {KilledName}", killerName, killedName);

        // Play on the first kill of the match.
        if (!_firstBlood)
        {
            _firstBlood = true;

            if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} drew first blood by killing {killedName}!");
            if (Configuration.Instance.EnablePvpFirstBloodEvent)
            {
                AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.FirstBlood);
            }
        }

        // Update multikills.
        // Multikills are performed within a 15-second window. The timer is refreshed on kill.
        // Resets to 0 on death.
        if (!_multikillCooldowns.TryGetValue(killerName, out DateTimeOffset multikillExpireTime) ||
            multikillExpireTime <= killTime)
        {
            _multikills[killerName] = 1;
        }
        else if (multikillExpireTime > killTime)
        {
            _multikills[killerName] += 1;
        }

        _multikills[killedName] = 0;
        _multikillCooldowns[killedName] = DateTimeOffset.UtcNow;
        _multikillCooldowns[killerName] = DateTimeOffset.UtcNow.AddSeconds(15);

        DalamudService.Log.Verbose("{KillerName} multikill streak: {Count}", killerName, _multikills[killerName]);

        bool multikills = Configuration.Instance.EnablePvpMultikillsEvent;

        switch (_multikills[killerName])
        {
            case 0:
            case 1:
                break;
            case 2:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} got a double kill!");
                if (multikills) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.Multikill2);
                break;
            case 3:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} has a TRIPLE kill!");
                if (multikills) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.Multikill3);
                break;
            case 4:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} earned an ULTRA KILL!");
                if (multikills) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.Multikill4);
                break;
            default:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} IS ON A RAMPAGE!!");
                if (multikills) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.Multikill5);
                break;
        }

        // Update kill streaks.
        // Resets to 0 on death.
        if (!_killStreaks.TryGetValue(killedName, out uint killedLastStreak)) killedLastStreak = 0;

        _killStreaks[killedName] = 0;
        if (!_killStreaks.TryAdd(killerName, 1)) _killStreaks[killerName] += 1;

        bool playKillStreaks = Configuration.Instance.EnablePvpKillStreaksEvent;

        DalamudService.Log.Verbose("{KillerName} kill streak: {Count}", killerName, _killStreaks[killerName]);

        switch (_killStreaks[killerName])
        {
            case 0:
            case 1:
            case 2:
                break;
            case 3:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} is on a killing spree!");
                if (playKillStreaks) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.KillStreak3);
                break;
            case 4:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} is dominating!");
                if (playKillStreaks) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.KillStreak4);
                break;
            case 5:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} is on a mega kill streak!");
                if (playKillStreaks) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.KillStreak5);
                break;
            case 6:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} is unstoppable!");
                if (playKillStreaks) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.KillStreak6);
                break;
            case 7:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} is wicked sick!");
                if (playKillStreaks) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.KillStreak7);
                break;
            case 8:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} is on a monster kill streak!");
                if (playKillStreaks) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.KillStreak8);
                break;
            case 9:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} is GODLIKE!");
                if (playKillStreaks) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.KillStreak9);
                break;
            default:
                if (chatLogKillStreaks) DalamudService.ChatGui.Print($"{killerName} is beyond GODLIKE, somebody stop them!!");
                if (playKillStreaks) AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.KillStreak10);
                break;
        }

        // Post a chat message if someone ended a kill streak.
        if (!chatLogKillStreaks) return;

        switch (killedLastStreak)
        {
            case 0:
            case 1:
            case 2:
                break;
            case 3:
                DalamudService.ChatGui.Print($"{killerName} ended {killedName}'s killing spree!");
                break;
            case 4:
                DalamudService.ChatGui.Print($"{killerName} ended {killedName}'s dominating streak!");
                break;
            case 5:
                DalamudService.ChatGui.Print($"{killerName} ended {killedName}'s mega kill streak!");
                break;
            case 6:
                DalamudService.ChatGui.Print($"{killerName} ended {killedName}'s unstoppable streak!");
                break;
            case 7:
                DalamudService.ChatGui.Print($"{killerName} ended {killedName}'s wicked sick streak!");
                break;
            case 8:
                DalamudService.ChatGui.Print($"{killerName} ended {killedName}'s monster kill streak!");
                break;
            case 9:
                DalamudService.ChatGui.Print($"{killerName} ended {killedName}'s GODLIKE streak!");
                break;
            default:
                DalamudService.ChatGui.Print($"{killerName} ended {killedName}'s beyond GODLIKE streak!");
                break;
        }
    }

    private unsafe byte ContentDirectorNetworkMessageDetour(IntPtr a1, IntPtr a2, ushort* a3)
    {
        try
        {
            // Crystalline Conflict countdown starts at 30s
            int pvpCountdownLength = 30_000;
            // Frontline starts countdown starts at 45s
            if (_frontlineTerritoryIds.Contains(TerritoryService.Instance.CurrentTerritory?.RowId)) pvpCountdownLength = 45_000;
            // Rival Wings starts countdown starts at 45s
            else if (_rivalWingsTerritoryIds.Contains(TerritoryService.Instance.CurrentTerritory?.RowId)) pvpCountdownLength = 45_000;

            var cat = *a3;
            var updateType = *(uint*)(a3 + 4);

            switch (cat)
            {
                // Start PvP Countdown
                case 0x6D when updateType == 0x40000004:
                    if (Configuration.Instance.EnablePvpCountdownStartEvent)
                    {
                        Task.Delay(pvpCountdownLength - 30_000).ContinueWith(_ =>
                        {
                            AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.CountdownStart);
                        });
                    }

                    if (Configuration.Instance.EnablePvpCountdown10Event)
                    {

                        Task.Delay(pvpCountdownLength - 10_000).ContinueWith(_ =>
                        {
                            AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.Countdown10);
                        });
                    }

                    break;
                // PvP win
                case 0x354 when updateType == 0x5030101:
                    if (Configuration.Instance.EnablePvpWinEvent)
                    {
                        Task.Delay(3_000).ContinueWith(_ =>
                        {
                            AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.PvpWin);
                        });
                    }

                    break;
                // PvP loss
                case 0x354 when updateType == 0x4030101:
                    if (Configuration.Instance.EnablePvpLossEvent)
                    {
                        Task.Delay(3_000).ContinueWith(_ =>
                        {
                            AudioService.Instance.PlayRandomSoundFromCategory(AudioEvent.Failure);
                        });
                    }

                    break;
            }
        }
        catch (Exception e)
        {
            DalamudService.Log.Error(e, "Error in PvpEvent.ContentDirectorNetworkMessageDetour");
        }
        return _contentDirectorNetworkMessageHook!.Original(a1, a2, a3);
    }

    private void OnTerritoryChanged(TerritoryType? territoryType)
    {
        if (DalamudService.ClientState.IsPvPExcludingDen) OnEnterPvP();
        else if (DalamudService.ClientState.IsPvP) OnLeavePvp();
    }

    private static bool IsInParty(PlayerPayload playerPayload)
    {
        return DalamudService.PartyList.Any(
            player =>
                XivUtility.GetFullPlayerName(player) == XivUtility.GetFullPlayerName(playerPayload));
    }

    private static bool IsInPartyAndProbablyDead(PlayerPayload playerPayload)
    {
        return DalamudService.PartyList.Any(
            player =>
                XivUtility.GetFullPlayerName(player) == XivUtility.GetFullPlayerName(playerPayload) &&
                player.CurrentHP <= 0);
    }

    private void ResetPvp()
    {
        DalamudService.Log.Debug("Resetting PvP");

        _firstBlood = false;

        _killStreaks = new Dictionary<string, uint>();
        _multikills = new Dictionary<string, uint>();
        _multikillCooldowns = new Dictionary<string, DateTimeOffset>();
    }
}