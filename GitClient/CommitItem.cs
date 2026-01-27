using CommunityToolkit.Mvvm.ComponentModel;
using LibGit2Sharp;

namespace GitClient {
	public partial class CommitItem( Commit commit ) : ObservableObject {
		public Commit Commit { get; set; } = commit;

		[ObservableProperty]
		public partial bool IsSelected { get; set; }
	}
}
