using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LibGit2Sharp;

namespace GitClient {

	public partial class MainWindowViewModel : ObservableObject {

		public string Title => this.Repository?.Head.FriendlyName ?? "Not Attached Repository";

		[ObservableProperty]
		[NotifyPropertyChangedFor( nameof( Title ) )]
		[NotifyCanExecuteChangedFor( nameof( RunCommand ) )]
		public partial Repository? Repository { get; set; }


		[ObservableProperty]
		[NotifyCanExecuteChangedFor( nameof( ApplyCommand ) )]
		public partial string Path { get; set; }


		bool CanApply() => !string.IsNullOrEmpty( Path );

		[RelayCommand( CanExecute = nameof( CanApply ) )]
		private void Apply() {
			this.Repository = new Repository( Path );

			UpdateItems();
		}


		[ObservableProperty]
		public partial ObservableCollection<CommitItem> Items { get; set; }


		public void UpdateItems() {
			if( this.Repository == null ) return;

			this.Items = new ObservableCollection<CommitItem>(
					this.Repository.Commits.QueryBy(
						new CommitFilter {
							IncludeReachableFrom = this.Repository.Head,
							SortBy = CommitSortStrategies.Topological
						}
					).Select( x => new CommitItem( x ) )
			);
		}

		[ObservableProperty]
		public partial string NewDateTime { get; set; } = DateTime.Now.ToString();


		private bool CanRun() {
			return this.Repository != null;
		}

		[RelayCommand( CanExecute = nameof( CanRun ) )]
		private void Run() {
			if( this.Repository == null ) return;

			if( !DateTime.TryParse( NewDateTime, out var v ) ) {
				WeakReferenceMessenger.Default.Send( new WindowMessage( w => {
					System.Windows.MessageBox.Show( w, "日時の形式が不正です。", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error );
				} ) );

				return;
			}

			var newDate = new DateTimeOffset( v, TimeSpan.FromHours( 9 ) );  // +0900

			var signature = new Signature(
				this.Repository.Config.BuildSignature( DateTimeOffset.Now ).Name,
				this.Repository.Config.BuildSignature( DateTimeOffset.Now ).Email,
				newDate
			);

			// チェックされた項目を取得
			var selectedCommits = Items.Where( item => item.IsSelected ).Select( item => item.Commit ).ToDictionary( x => x.Id );

			if( selectedCommits.Count == 0 ) {
				WeakReferenceMessenger.Default.Send( new WindowMessage( w => {
					System.Windows.MessageBox.Show( w, "対象コミットを選択してください。", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error );
				} ) );
				return;
			}

			// 処理前のブランチを記憶(detached HEADの場合はnull)
			var originalBranch = this.Repository.Info.IsHeadDetached ? null : this.Repository.Head;

			var commits = this.Repository.Commits.QueryBy(
				new CommitFilter {
					IncludeReachableFrom = this.Repository.Head,
					SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse
				}
			);

			bool newBranch = false;
			foreach( var commit in commits ) {
				if( newBranch ) {
					if( selectedCommits.TryGetValue( commit.Id, out var obj ) ) {
						this.Repository.CherryPick( obj, signature, new CherryPickOptions() { CommitOnSuccess = false } );
						this.Repository.Commit( obj.Message, signature, signature, new() { AllowEmptyCommit = true } );

					} else {
						this.Repository.CherryPick( commit, commit.Committer, new CherryPickOptions() { CommitOnSuccess = false } );
						this.Repository.Commit( commit.Message, commit.Author, commit.Committer, new() { AllowEmptyCommit = true } );
					}

				} else {
					if( selectedCommits.TryGetValue( commit.Id, out var obj ) ) {
						newBranch = true;
						Commands.Checkout( this.Repository, obj );
						this.Repository.Commit( obj.Message, signature, signature, new() { AmendPreviousCommit = true, AllowEmptyCommit = true } );
					}
				}
			}

			if( newBranch && originalBranch != null ) {
				var branchName = originalBranch.FriendlyName;
				var newTip = this.Repository.Head.Tip;

				// 元のブランチをバックアップ名にリネーム(重複時は連番を付与)
				var backupName = $"{branchName}_backup";
				for( int i = 2; this.Repository.Branches[backupName] != null; i++ ) {
					backupName = $"{branchName}_backup_{i}";
				}
				this.Repository.Branches.Rename( originalBranch, backupName );

				// 処理後のコミットに元のブランチ名を付けてチェックアウト
				var renamedBranch = this.Repository.Branches.Add( branchName, newTip );
				Commands.Checkout( this.Repository, renamedBranch );

				OnPropertyChanged( nameof( Title ) );
			}

			UpdateItems();
		}
	}
}
