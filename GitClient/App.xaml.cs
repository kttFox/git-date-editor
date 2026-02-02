using System.Configuration;
using System.Data;
using System.Windows;

namespace GitClient
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup( StartupEventArgs e ) {
            base.OnStartup( e );

            this.DispatcherUnhandledException += ( sender, args ) => {
                App.Current.Shutdown( -1 );
			};
        }
    }

}
