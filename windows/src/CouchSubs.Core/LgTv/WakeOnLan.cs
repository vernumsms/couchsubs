// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Sockets;

namespace CouchSubs.Core.LgTv;

/// <summary>
/// Ağdan uyandırma ("sihirli paket"). Samsung TV bekleme modundayken bununla açılır (TV'de "Mobil cihazla aç" /
/// "Ağ bekleme modu" açık olmalı; çoğu modelde varsayılan açıktır).
/// </summary>
public static class WakeOnLan
{
    /// <summary>6 × 0xFF, ardından MAC adresi 16 kez.</summary>
    public static byte[] MagicPacket(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("MAC 6 bayt olmalı", nameof(mac));
        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) mac.CopyTo(packet, 6 + i * 6);
        return packet;
    }

    /// <summary>"e8-aa-cb-d6-0f-80" ya da "E8:AA:…" biçimindeki adresi okur; okunamazsa null.</summary>
    public static byte[]? ParseMac(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hex = text.Replace("-", "").Replace(":", "").Trim();
        if (hex.Length != 12) return null;
        try
        {
            var mac = Convert.FromHexString(hex);
            return mac.All(b => b == 0) ? null : mac;
        }
        catch (FormatException) { return null; }
    }

    public static string FormatMac(byte[] mac) => Convert.ToHexString(mac);

    /// <summary>Paketi TV'nin alt ağına (x.y.z.255) ve genel yayına, 9 ve 7 numaralı portlara üçer kez gönderir.</summary>
    public static async Task SendAsync(byte[] mac, string tvHost)
    {
        var packet = MagicPacket(mac);
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        if (IPAddress.TryParse(tvHost, out var tv) && tv.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = tv.GetAddressBytes();
            bytes[3] = 255;
            targets.Insert(0, new IPAddress(bytes));
            targets.Add(tv); // modem ARP kaydını tutuyorsa doğrudan da ulaşır
        }
        using var udp = new UdpClient { EnableBroadcast = true };
        for (var round = 0; round < 3; round++)
        {
            foreach (var target in targets)
                foreach (var port in new[] { 9, 7 })
                {
                    try { await udp.SendAsync(packet, new IPEndPoint(target, port)); }
                    catch (SocketException) { /* bu hedefe gönderilemedi, diğerleri denenir */ }
                }
            await Task.Delay(200);
        }
    }
}
