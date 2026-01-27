using System.Windows;

namespace GitClient {
	public class WindowMessage( Action<Window> action ) {
		public Action<Window> Action { get; } = action;
	}
}