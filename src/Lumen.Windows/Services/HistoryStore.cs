using System.Collections.ObjectModel;
using System.Text.Json;
using Lumen.Shared;
using Lumen.Windows.Infrastructure;
using Lumen.Windows.Models;

namespace Lumen.Windows.Services;

public sealed class HistoryStore : IDisposable
{
    private readonly string _historyPath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private CancellationTokenSource? _saveDebounce;
    private bool _disposed;

    public HistoryStore(SettingsStore settings)
    {
        _historyPath = Path.Combine(settings.DataDirectory, "history.json");
        Records = new ObservableCollection<LocalTransferRecord>(Load());
    }

    public ObservableCollection<LocalTransferRecord> Records { get; }

    public void Add(LocalTransferRecord record)
    {
        UiThread.Post(() =>
        {
            var existing = Records.FirstOrDefault(x => x.Id == record.Id);
            if (existing is not null)
            {
                Records.Remove(existing);
            }

            Records.Insert(0, record);
            while (Records.Count > 1000)
            {
                var removable = Records.LastOrDefault(x => x.IsTerminal);
                if (removable is null)
                {
                    break;
                }

                Records.Remove(removable);
            }
        });
        SaveSoon();
    }

    public LocalTransferRecord? Find(Guid id) => Records.FirstOrDefault(x => x.Id == id);

    public void Remove(Guid id)
    {
        UiThread.Post(() =>
        {
            var item = Records.FirstOrDefault(x => x.Id == id);
            if (item is not null)
            {
                Records.Remove(item);
            }
        });
        SaveSoon();
    }

    public void SaveSoon()
    {
        if (_disposed)
        {
            return;
        }

        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        _saveDebounce = new CancellationTokenSource();
        var token = _saveDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, token);
                await SaveAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }, token);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            var snapshot = await UiThread.InvokeAsync(() =>
                Records.Select(x => x.Snapshot()).ToList());
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, LumenJson.PrettyOptions);
            await FileUtilities.AtomicWriteAsync(_historyPath, json, ct);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private List<LocalTransferRecord> Load()
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<LocalTransferRecord>>(
                File.ReadAllText(_historyPath),
                LumenJson.Options) ?? [];
        }
        catch
        {
            try
            {
                File.Move(
                    _historyPath,
                    _historyPath + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            catch
            {
            }

            return [];
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        _saveLock.Dispose();
    }
}
