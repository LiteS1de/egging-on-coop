using System.Runtime.InteropServices;
using UnityEngine;

namespace EggCoop
{
    // Two UI languages. Every on-screen string is written as T("русский", "English") right where it is used,
    // so a switch in F9 takes effect on the next frame. "auto" follows Windows.
    public static class Lang
    {
        public static bool En;

        public static string T(string ru, string en) => En ? en : ru;

        [DllImport("kernel32.dll")] static extern ushort GetUserDefaultUILanguage();
        [DllImport("kernel32.dll")] static extern ushort GetUserDefaultLangID();   // the regional format

        // Russian, Ukrainian or Belarusian anywhere counts: many Russian speakers run an English Windows
        // with the Russian region or a Russian language pack on top.
        static bool RuId(ushort langId) { int p = langId & 0x3FF; return p == 0x19 || p == 0x22 || p == 0x23; }

        public static bool SystemIsRussian()
        {
            var l = Application.systemLanguage;
            if (l == SystemLanguage.Russian || l == SystemLanguage.Ukrainian || l == SystemLanguage.Belarusian) return true;
            try { return RuId(GetUserDefaultUILanguage()) || RuId(GetUserDefaultLangID()); }
            catch { return false; }
        }

        public static void Apply(string setting) =>
            En = setting == "en" || (setting != "ru" && !SystemIsRussian());
    }
}
