using UnityEngine;

namespace EggCoop
{
    // Streamer mode: nothing that leads to a player's address is drawn on screen. IPs (host list, network log,
    // toasts), invite codes (they encode the host's public IP) and the join field are masked; "Копировать" still
    // copies the real value, so the host can paste it to friends off stream.
    public partial class Plugin
    {
        bool Streamer => cfg.StreamerMode.Value;

        // Masks addresses and invite codes when streamer mode is on; text is unchanged otherwise.
        string Hide(string s)
        {
            return Streamer ? StreamerMask.Apply(s) : s;
        }

        void DrawStreamerToggle()
        {
            bool v = EggToggle(cfg.StreamerMode.Value, "Режим стримера — скрывать IP-адреса и коды на экране");
            if (v != cfg.StreamerMode.Value) { cfg.StreamerMode.Value = v; Toast(v ? "Режим стримера включён: адреса и коды скрыты" : "Режим стримера выключен"); }
        }
    }
}
