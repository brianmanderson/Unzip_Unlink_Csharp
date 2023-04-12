using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FolderWatcher
{
    public class Watcher
    {
        private bool folder_changed;
        public bool Folder_Changed
        {
            get
            {
                return folder_changed;
            }
            set
            {
                folder_changed = value;
            }
        }
        public Watcher(string directory)
        {
            FileSystemWatcher file_watcher = new FileSystemWatcher(directory);
            file_watcher.NotifyFilter = NotifyFilters.Attributes
                                 | NotifyFilters.CreationTime
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.FileName
                                 | NotifyFilters.LastAccess
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.Security
                                 | NotifyFilters.Size;
            file_watcher.Changed += OnChanged;
            file_watcher.EnableRaisingEvents = true;
        }
        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType != WatcherChangeTypes.Changed)
            {
                folder_changed = false;
                return;
            }
            folder_changed = true;
        }
    }
}
