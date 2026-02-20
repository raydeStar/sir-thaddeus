using System;
using System.IO;
using System.Windows.Markup;

public class Program {
    public static void Main() {
        try {
            using (var fs = new FileStream(""apps/desktop-runtime/SirThaddeus.DesktopRuntime/CommandPaletteWindow.xaml"", FileMode.Open))
            {
                var context = new ParserContext();
                // We just want to see if XamlReader throws. 
                // XamlReader.Load will try to construct types.
                XamlReader.Load(fs);
                Console.WriteLine(""Loaded successfully"");
            }
        } catch (Exception ex) {
            Console.WriteLine(ex.ToString());
        }
    }
}
