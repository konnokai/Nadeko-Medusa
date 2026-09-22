using System.Collections.Concurrent;

namespace MuteReborn.Services;

/// <summary>
/// 進行中的勞改對「發起者的甦生券」保留額度。
/// 扣券改成禁言成功後才執行，因此加時階段與其他消費路徑都必須看得到已保留的張數，
/// 否則同一張券會先被加時花掉、結束後又被扣一次，餘額變成負數。
/// 保留狀態只存在記憶體：程序重啟時進行中的流程也一併中斷。
/// </summary>
public sealed class TicketReservations
{
    private readonly ConcurrentDictionary<string, int> _reserved = new();

    /// <summary>
    /// 嘗試為 <paramref name="key"/> 保留一張券。
    /// <paramref name="balance"/> 為當下的資料庫餘額，扣除既有保留量後仍大於 0 才保留成功。
    /// </summary>
    public bool TryReserve(string key, int balance)
    {
        while (true)
        {
            _reserved.TryGetValue(key, out var reserved);

            if (balance - reserved <= 0)
                return false;

            if (reserved == 0)
            {
                if (_reserved.TryAdd(key, 1))
                    return true;
            }
            else if (_reserved.TryUpdate(key, reserved + 1, reserved))
            {
                return true;
            }
        }
    }

    /// <summary>釋放 <paramref name="key"/> 的一張保留額度；沒有保留時不做任何事。</summary>
    public void Release(string key)
    {
        while (_reserved.TryGetValue(key, out var reserved))
        {
            if (reserved <= 1)
            {
                if (_reserved.TryRemove(new KeyValuePair<string, int>(key, reserved)))
                    return;
            }
            else if (_reserved.TryUpdate(key, reserved - 1, reserved))
            {
                return;
            }
        }
    }

    /// <summary>目前保留的張數。</summary>
    public int ReservedFor(string key)
        => _reserved.TryGetValue(key, out var reserved) ? reserved : 0;
}
