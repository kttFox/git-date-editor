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
			while( true ) {
				try {
					this.Repository = new Repository( Path );
					break;
				} catch ( LibGit2Sharp.RepositoryNotFoundException ) {
					if( !string.IsNullOrEmpty( Path ) ) {
						var p = System.IO.Path.GetDirectoryName( Path )!;
						if( Path == p ) {
							break;
						}
						Path = p;
					}
				}
			}

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
						CommitWithSign( obj.Message, signature, signature,
							this.Repository.ObjectDatabase.CreateTree( this.Repository.Index ),
							new[] { this.Repository.Head.Tip } );

					} else {
						this.Repository.CherryPick( commit, commit.Committer, new CherryPickOptions() { CommitOnSuccess = false } );
						CommitWithSign( commit.Message, commit.Author, commit.Committer,
							this.Repository.ObjectDatabase.CreateTree( this.Repository.Index ),
							new[] { this.Repository.Head.Tip } );
					}

				} else {
					if( selectedCommits.TryGetValue( commit.Id, out var obj ) ) {
						newBranch = true;
						Commands.Checkout( this.Repository, obj );
						// amend相当:同じツリー・親のまま日時のみ変更したコミットを作り直す
						CommitWithSign( obj.Message, signature, signature, obj.Tree, obj.Parents );
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


		/// <summary>
		/// コミットを作成し、設定(commit.gpgsign)が有効ならGPG署名を付与してHEADを進める。
		/// </summary>
		private void CommitWithSign( string message, Signature author, Signature committer, Tree tree, IEnumerable<Commit> parents ) {
			var repo = this.Repository!;

			var buffer = BuildCommitBuffer( author, committer, message, tree, parents );

			ObjectId id;
			var gpgSignature = TrySign( repo, buffer );
			if( gpgSignature != null ) {
				id = repo.ObjectDatabase.CreateCommitWithSignature( buffer, gpgSignature );
			} else {
				id = repo.ObjectDatabase.CreateCommit( author, committer, message, tree, parents, false ).Id;
			}

			// HEAD(detached)を新しいコミットへ移動し、作業ツリー・インデックスを一致させる
			repo.Refs.UpdateTarget( "HEAD", id.Sha );
			repo.Reset( ResetMode.Hard, repo.Lookup<Commit>( id ) );

			// cherry-pick途中状態のファイルを掃除
			foreach( var name in new[] { "CHERRY_PICK_HEAD", "MERGE_MSG" } ) {
				var p = System.IO.Path.Combine( repo.Info.Path, name );
				if( System.IO.File.Exists( p ) ) System.IO.File.Delete( p );
			}
		}


		/// <summary>
		/// 署名対象となる生のコミットバッファ(git cat-file commit と同形式)を組み立てる。
		/// </summary>
		private static string BuildCommitBuffer( Signature author, Signature committer, string message, Tree tree, IEnumerable<Commit> parents ) {
			static string Format( Signature s ) {
				var off = s.When.Offset;
				var sign = off < TimeSpan.Zero ? "-" : "+";
				return $"{s.Name} <{s.Email}> {s.When.ToUnixTimeSeconds()} {sign}{Math.Abs( off.Hours ):00}{Math.Abs( off.Minutes ):00}";
			}

			var sb = new StringBuilder();
			sb.Append( $"tree {tree.Sha}\n" );
			foreach( var p in parents ) {
				sb.Append( $"parent {p.Sha}\n" );
			}
			sb.Append( $"author {Format( author )}\n" );
			sb.Append( $"committer {Format( committer )}\n" );
			sb.Append( '\n' );
			sb.Append( message );

			return sb.ToString();
		}


		/// <summary>
		/// コミットバッファをgpgで署名する。署名不要なら null を返す。
		/// </summary>
		private static string? TrySign( Repository repo, string commitBuffer ) {
			if( !repo.Config.GetValueOrDefault( "commit.gpgsign", false ) ) return null;

			var format = repo.Config.GetValueOrDefault( "gpg.format", "openpgp" );
			if( format != "openpgp" ) {
				throw new InvalidOperationException( $"gpg.format={format} は未対応です(openpgpのみ対応)。" );
			}

			var program = repo.Config.GetValueOrDefault( "gpg.program", "gpg" );
			var key = repo.Config.GetValueOrDefault<string>( "user.signingkey", "" );
			if( string.IsNullOrEmpty( key ) ) {
				var s = repo.Config.BuildSignature( DateTimeOffset.Now );
				key = $"{s.Name} <{s.Email}>";
			}

			var psi = new System.Diagnostics.ProcessStartInfo {
				FileName = program,
				Arguments = $"--batch -bsau \"{key}\"",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				StandardOutputEncoding = Encoding.ASCII,
			};

			using var proc = System.Diagnostics.Process.Start( psi )
				?? throw new InvalidOperationException( "gpgを起動できませんでした。" );

			var bytes = Encoding.UTF8.GetBytes( commitBuffer );
			proc.StandardInput.BaseStream.Write( bytes, 0, bytes.Length );
			proc.StandardInput.Close();

			var sig = proc.StandardOutput.ReadToEnd();
			var err = proc.StandardError.ReadToEnd();
			proc.WaitForExit();

			if( proc.ExitCode != 0 || !sig.Contains( "-----BEGIN PGP SIGNATURE-----" ) ) {
				throw new InvalidOperationException( $"GPG署名に失敗しました。\n{err}" );
			}

			return sig;
		}
	}
}
