using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ETABSv1;

namespace SectionCutter
{

    class cPlugin
    {
        public void Main(ref cSapModel SapModel, ref cPluginCallback ISapPlugin)
        {
            // Copy to locals (safe inside thread)
            var localSapModel = SapModel;
            var localPlugin = ISapPlugin;

            Thread uiThread = new Thread(() =>
            {
                var app = new Application();
                var window = new MainWindow(localSapModel, localPlugin);
                app.Run(window);
            });

            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
        }

        public long Info(ref string Text)
        {
            Text = "Section Cutter Tool. Diaphragm Slicer";
            return 0;
        }
    }
}
