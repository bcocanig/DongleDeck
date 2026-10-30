using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DongleDeck {
    public static class Theme {
        static readonly string SettingsPath=Path.Combine(AppLog.DirectoryPath,"theme.txt");
        public static bool IsDark {get;private set;}
        public static bool Load() {
            try {return !File.Exists(SettingsPath) || File.ReadAllText(SettingsPath).Trim()!="light";}
            catch(IOException) {return true;} catch(UnauthorizedAccessException) {return true;}
        }
        public static void Apply(Window window,bool dark,bool save) {
            IsDark=dark;DeviceSlot.DarkTheme=dark;
            string[] keys={"Canvas","Card","Text","Muted","Border","Subtle","Accent","AccentSoft","Success","SuccessSoft","Danger","DangerSoft","ScanSurface","ScanBorder"};
            string[] colors=dark
                ?new[]{"#070C18","#0D1424","#F1F5F9","#A6B5C8","#263448","#162439","#00F0FF","#0D3442","#7AE1AA","#102C26","#FFA4AE","#482C34","#10251F","#3F7760"}
                :new[]{"#F8FAFC","#FFFFFF","#0F172A","#475569","#D6DFEB","#EEF3F9","#0076CE","#E8F5FF","#216D43","#EAF6EF","#A32B3A","#FBECEE","#EDF8F1","#BCDCC9"};
            for(int i=0;i<keys.Length;i++) window.Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            if(save) try {Directory.CreateDirectory(AppLog.DirectoryPath);File.WriteAllText(SettingsPath,dark?"dark":"light");}
            catch(IOException) {} catch(UnauthorizedAccessException) {}
        }
    }
}
