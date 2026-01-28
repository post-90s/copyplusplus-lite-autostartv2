using System;
using System.Linq;
using System.Reflection;
using CopyPlusPlus.Properties;
using Microsoft.Win32;

namespace CopyPlusPlus
{
    internal static class AutoStartManager
    {
        private const string RunPath = @"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string AppName = "CopyPlusPlus";
        private const int SwitchIndex = 5; // keep compatibility with the original project

        public static bool GetEnabled()
        {
            try
            {
                var list = Settings.Default.SwitchCheck?.Cast<string>().ToList();
                if (list == null) return false;
                if (SwitchIndex < 0 || SwitchIndex >= list.Count) return false;
                return bool.TryParse(list[SwitchIndex], out var v) && v;
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                var sc = Settings.Default.SwitchCheck;
                if (sc == null) return;

                while (sc.Count <= SwitchIndex) sc.Add(string.Empty);
                sc[SwitchIndex] = enabled.ToString();
                Settings.Default.Save();
            }
            catch
            {
                // ignored
            }

            ApplyRegistry(enabled);
        }

        /// <summary>
        /// Align the registry entry with the saved setting and current exe path.
        /// Call this on each app start to ensure correctness.
        /// </summary>
        public static void ApplyFromSettings()
        {
            ApplyRegistry(GetEnabled());
        }

        private static void ApplyRegistry(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true))
                {
                    if (key == null) return;

                    if (enabled)
                    {
                        var exe = Assembly.GetExecutingAssembly().Location;
                        key.SetValue(AppName, exe + " /AutoStart");
                    }
                    else
                    {
                        key.DeleteValue(AppName, throwOnMissingValue: false);
                    }
                }
            }
            catch
            {
                // ignored (permissions, policy, etc.)
            }
        }
    }
}
