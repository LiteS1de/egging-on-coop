using UnityEngine;

namespace EggCoop
{
    // Twitch connection (device login, channel point rewards, bits). Needs the mod's Twitch Client ID: next step.
    public partial class Plugin
    {
        void DrawTwitchRow() => GUILayout.Label("Twitch: подключение появится в следующей версии. Пока эффекты можно проверить кнопками «Тест».", sDim);
    }
}
