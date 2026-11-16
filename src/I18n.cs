using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hydra
{
    // Локализация: исходные строки в коде — русские, L() возвращает перевод для текущего языка.
    public static partial class I18n
    {
        public static string Lang = "ru";

        public static void Init(string setting)
        {
            setting = (setting ?? "auto").ToLowerInvariant();
            if (setting == "ru" || setting == "en") Lang = setting;
            else Lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
        }

        public static string L(string ru)
        {
            if (Lang == "ru" || ru == null) return ru;
            string en;
            return En.TryGetValue(ru, out en) ? en : ru;
        }

        static readonly Dictionary<string, string> En = new Dictionary<string, string>(StringComparer.Ordinal);

        static void T(string ru, string en) { En[ru] = en; }
    }
}
