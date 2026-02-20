using System;
using System.IO;
using System.Windows.Markup;
using System.Xml;

namespace XamlTest {
    class Program {
        [STAThread]
        static void Main(string[] args) {
            try {
                using (FileStream fs = new FileStream(""apps/desktop-runtime/SirThaddeus.DesktopRuntime/CommandPaletteWindow.xaml"", FileMode.Open))
                {
                    XamlReader.Load(fs);
                    Console.WriteLine(""XAML Loaded Successfully!"");
                }
            } catch (Exception ex) {
                Console.WriteLine(""Error loading XAML:"");
                Console.WriteLine(ex.ToString());
            }
        }
    }
}
