using System;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace StanleyParableXiv.Services;

public class TerritoryService : IDisposable
{
    public static TerritoryService Instance { get; private set; } = new();

    public TerritoryType? CurrentTerritory;

    public event OnTerritoryChangedDelegate? TerritoryChanged;

    public delegate void OnTerritoryChangedDelegate(TerritoryType? territoryType);

    private uint? _currentTerritoryRowId;

    public TerritoryService()
    {
        DalamudService.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        DalamudService.Framework.Update -= OnFrameworkUpdate;

        GC.SuppressFinalize(this);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        uint newTerritoryId = DalamudService.ClientState.TerritoryType;
        if (_currentTerritoryRowId == newTerritoryId) return;

        bool currentTerritoryExists = DalamudService.DataManager.Excel
            .GetSheet<TerritoryType>()
            .TryGetRow(newTerritoryId, out TerritoryType nextTerritory);

        DalamudService.Log.Debug("Territory changed: {LastTerritory} -> {NewTerritory}",
            _currentTerritoryRowId ?? 0, newTerritoryId);

        _currentTerritoryRowId = newTerritoryId;
        CurrentTerritory = currentTerritoryExists ? nextTerritory : null;

        TerritoryChanged?.Invoke(CurrentTerritory);
    }
}