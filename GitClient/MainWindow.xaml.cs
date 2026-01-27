using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace GitClient {
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window {
		public MainWindow() {
			InitializeComponent();

			WeakReferenceMessenger.Default.Register<WindowMessage>( this, ( s, m ) => {
				m.Action( this );
			} );
		}

		private void DataGrid_KeyDown( object sender, KeyEventArgs e ) {
			if( e.Key != Key.Space ) return;

			var dg = (DataGrid)sender;
			if( dg.SelectedItems.OfType<CommitItem>().All( x => x.IsSelected ) ) {
				foreach( var item in dg.SelectedItems.OfType<CommitItem>() ) {
					item.IsSelected = false;
				}
			} else {
				foreach( var item in dg.SelectedItems.OfType<CommitItem>() ) {
					item.IsSelected = true;
				}
			}
		}
	}
}