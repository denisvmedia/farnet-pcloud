using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Collections;
using FarNet.Forms;
using Azi.Amazon.CloudDrive;
using Azi.Amazon.CloudDrive.JsonObjects;
using Newtonsoft.Json;

namespace FarNet.PCloud
{
    /// <summary>
    /// TODO
    /// </summary>
    class ACDClient : ITokenUpdateListener
    {
        /// <summary>
        /// TODO
        /// </summary>
        private AmazonDrive amazon;

        private bool _authenticated = false;

        /// <summary>
        /// TODO
        /// </summary>
        public bool Authenticated {
            get { return _authenticated; }
            set { _authenticated = value; }
        }

        /// <summary>
        /// TODO
        /// </summary>
        private static readonly AmazonNodeKind[] FsItemKinds = { AmazonNodeKind.FILE, AmazonNodeKind.FOLDER };

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="cs"></param>
        /// <param name="interactiveAuth"></param>
        /// <returns></returns>
        public async Task<AmazonDrive> Authenticate(CancellationToken cs, bool interactiveAuth = true)
        {
            var Settings = ACDSettings.Default.GetData();

            if (Settings.AuthToken != null)
            {
                Authenticated = true;
                var token = new AuthToken();
                token.access_token = Settings.AuthToken;
                amazon = new AmazonDrive(Settings.ClientId, Settings.ClientSecret, token);
                return amazon;
            }

            amazon = new AmazonDrive(Settings.ClientId, Settings.ClientSecret);
            amazon.OnTokenUpdate = this;

            if (await amazon.AuthenticationByExternalBrowser(TimeSpan.FromMinutes(2), cs, "http://localhost:{0}/signin/"))
            {
                Authenticated = true;
                return amazon;
            }

            return null;
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="itemPath"></param>
        /// <param name="CacheBypass"></param>
        /// <returns></returns>
        public async Task<FSItem> FetchNode(string itemPath, bool CacheBypass = false)
        {
            FSItem item = null;
            if (!CacheBypass)
            {
                item = CacheStorage.GetItemByPath(itemPath);
            }

            if (item != null)
            {
                return item;
            }

            if (itemPath == string.Empty)
            {
                itemPath = "\\";
            }

            AmazonNode node;
            // try as a directory
            node = await amazon.Nodes.GetNodeByPath(itemPath, true);

            if (node == null)
            {
                // try as a file
                node = await amazon.Nodes.GetNodeByPath(itemPath, false);

                if (node == null)
                {
                    return null;
                }
            }

            item = FSItem.FromNode(itemPath, node);
            CacheStorage.AddItem(item);

            return item;
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="folderPath"></param>
        /// <returns></returns>
        public async Task<IList<FSItem>> GetDirItems(string folderPath)
        {
            var items = CacheStorage.GetItemsByParentPath(folderPath);

            if (items != null)
            {
                return items;
            }

            var node = await amazon.Nodes.GetNodeByPath(folderPath, true);

            if (node == null || node.contents.Count == 0)
            {
                items = new List<FSItem>(0);
                return items;
            }

            items = new List<FSItem>(node.contents.Count);

            foreach (var subnode in node.contents)
            {
                items.Add(FSItem.FromNode(node.path.TrimEnd('/') + "/" + subnode.name, subnode));
            }

            CacheStorage.AddItems(folderPath, items);

            return items;
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="item"></param>
        /// <param name="dest"></param>
        /// <param name="form"></param>
        /// <param name="wh"></param>
        /// <param name="progress"></param>
        /// <param name="totalsize"></param>
        /// <returns></returns>
        public async Task<long> DownloadFile(FSItem item, string dest, Tools.ProgressForm form, EventWaitHandle wh, long progress, long totalsize)
        {
            if (item.IsDir)
            {
                form.Activity = string.Format("{0} {1})", "Creating directory", Utility.ShortenString(dest, 20));
                Directory.CreateDirectory(dest);
                return progress;
            }

            using (var fs = new FileStream(dest, FileMode.Create))
            {
                await amazon.Files.Download(item.Id, fs, null, null, 4096, (long position) =>
                {
                    wh.WaitOne();

                    if (form.IsClosed)
                    {
                        fs.Dispose();
                        try
                        {
                            File.Delete(dest);
                        }
                        catch { }
                        throw new TaskCanceledException();
                    }

                    form.Activity = Progress.GetActivityProgress(dest, position, item.Length, progress + position, totalsize);

                    form.SetProgressValue(progress + position, totalsize);

                    return position;
                });

                return progress + item.Length;
            }
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="item"></param>
        /// <param name="form"></param>
        /// <returns></returns>
        public async Task DeleteFile(FSItem item, Tools.ProgressForm form)
        {
            form.Activity = Utility.ShortenString(item.Path, 20);
            await amazon.Nodes.Trash(item.Id, item.IsDir);

            CacheStorage.RemoveItem(item);
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="parent"></param>
        /// <param name="allowExisting"></param>
        /// <returns></returns>
        public async Task<FSItem> CreateDirectory(string filePath, FSItem parent = null, bool allowExisting = true)
        {
            if (filePath == "\\" || filePath == ".." || filePath == ".")
            {
                return null;
            }
            var dir = Path.GetDirectoryName(filePath);
            if (dir == ".." || dir == ".")
            {
                return null;
            }

            if (parent == null)
            {
                parent = await FetchNode(dir);
                if (parent == null)
                {
                    return null;
                }
            }

            var name = Path.GetFileName(filePath);
            AmazonNode node = null;

            try
            {
                node = await amazon.Nodes.CreateFolder(parent.Id, name);
            }
            catch (Azi.Tools.HttpWebException x)
            {
                if (x.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    if (!allowExisting)
                    {
                        throw;
                    }
                }
                else
                {
                    throw;
                }
            }

            if (node == null) // in case of duplicate; other cases are re-thrown
            {
                node = await amazon.Nodes.GetNodeByPath(filePath, true);
            }

            if (node == null)
            {
                throw new InvalidOperationException("Could not retrieve node information " + filePath);
            }

            var item = FSItem.FromNode(filePath, node);

            CacheStorage.AddItem(item);

            return item;
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="FileData"></param>
        /// <returns></returns>
        public async Task<long> UploadNewFile(UploadFileData FileData)
        {
            var itemLength = new FileInfo(FileData.File.Name).Length;
            var totalBytes = Utility.BytesToString(itemLength);
            var filename = Path.GetFileName(FileData.File.Name);
            var fileUpload = new FileUpload();
            fileUpload.AllowDuplicate = false;
            fileUpload.ParentId = FileData.ParentItem.Id;
            fileUpload.FileName = filename;
            fileUpload.StreamOpener = () => new FileStream(FileData.File.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            fileUpload.Progress = (long position) =>
            {
                FileData.PauseEvent.WaitOne();
                FileData.Form.Activity = Progress.GetActivityProgress(
                    FileData.File.Name,
                    position,
                    itemLength,
                    FileData.TotalProgress + position,
                    FileData.TotalSize,
                    FileData.TimestampStartOne,
                    FileData.TimestampStartTotal
                    );
                FileData.Form.SetProgressValue(FileData.TotalProgress + position, FileData.TotalSize);

                return position;
            };
            var cs = new CancellationTokenSource();
            var token = cs.Token;
            fileUpload.CancellationToken = token;
            
            var dlgProp = typeof(Tools.ProgressForm).GetProperty("Dialog", BindingFlags.Instance | BindingFlags.NonPublic);
            var dlg = dlgProp?.GetValue(FileData.Form) as IDialog;
            if (dlg != null)
            {
                dlg.Closed += (object sender, AnyEventArgs e) =>
                {
                    if (e.Control == null)
                    {
                        cs.Cancel(true);
                    }
                };
            }    

            var result = await amazon.Files.UploadNew(fileUpload);

            foreach (var node in result.metadata)
            {
                CacheStorage.AddItem(FSItem.FromNode(Path.Combine(FileData.ParentItem.Path, filename), node));
                if (node.size != null)
                {
                    FileData.TotalProgress += node.size.Value;
                }
            }

            return FileData.TotalProgress /*+ node.Length*/;
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="FileData"></param>
        /// <returns></returns>
        public async Task<long> ReplaceFile(UploadFileData FileData)
        {
            var itemLength = new FileInfo(FileData.File.Name).Length;
            var filename = Path.GetFileName(FileData.File.Name);
            var fileUpload = new FileUpload();
            var cs = new CancellationTokenSource();
            var token = cs.Token;
            fileUpload.CancellationToken = token;
            fileUpload.AllowDuplicate = false;
            fileUpload.ParentId = FileData.ParentItem.Id;

            var ACDFilePath = FileData.RemoteFileName;
            // for upload we need a node id to replace
            if (string.IsNullOrEmpty(ACDFilePath))
            {
                ACDFilePath = Path.Combine(FileData.ParentItem.Path, filename);
            }
            var node = await FetchNode(ACDFilePath);
            if (node == null)
            {
                throw new FileNotFoundException("Remote file " + ACDFilePath + " not found");
            }
            fileUpload.FileName = node.Name;

            fileUpload.StreamOpener = () => new FileStream(FileData.File.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            fileUpload.Progress = (long position) =>
            {
                FileData.PauseEvent.WaitOne();
                FileData.Form.Activity = Progress.GetActivityProgress(
                    FileData.File.Name,
                    FileData.TotalProgress,
                    itemLength,
                    FileData.TotalProgress + position,
                    FileData.TotalSize,
                    FileData.TimestampStartOne,
                    FileData.TimestampStartTotal
                    );
                FileData.Form.SetProgressValue(FileData.TotalProgress + position, FileData.TotalSize);

                return position;
            };

            var dlgProp = typeof(Tools.ProgressForm).GetProperty("Dialog", BindingFlags.Instance | BindingFlags.NonPublic);
            var dlg = dlgProp?.GetValue(FileData.Form) as IDialog;
            if (dlg != null)
            {
                dlg.Closed += (object sender, AnyEventArgs e) =>
                {
                    if (e.Control == null)
                    {
                        cs.Cancel(true);
                    }
                };
            }    

            var result = await amazon.Files.Overwrite(fileUpload);

            foreach (var newNode in result.metadata)
            {
                CacheStorage.AddItem(FSItem.FromNode(Path.Combine(FileData.ParentItem.Path, filename), newNode));
            }

            return FileData.TotalProgress /* + resultNode.Length */;
        }
        
        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="item"></param>
        /// <param name="newParent"></param>
        /// <returns></returns>
        public async Task<bool> MoveFile(FSItem item, string newParent)
        {
            if (!Utility.IsValidPathname(newParent))
            {
                return false;
            }

            var newParentNode = await FetchNode(newParent);

            return await MoveFile(item, newParentNode);
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="item"></param>
        /// <param name="newParentNode"></param>
        /// <returns></returns>
        public async Task<bool> MoveFile(FSItem item, FSItem newParentNode)
        {
            if (newParentNode == null)
            {
                return false; // TODO: throw an exception
            }

            if (!newParentNode.IsDir)
            {
                return false; // TODO: throw an exception
            }

            await amazon.Nodes.Move(item.Id, newParentNode.Id, item.IsDir);

            CacheStorage.RemoveItem(item);
            CacheStorage.RemoveItems(newParentNode.Path);

            return true;
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="item"></param>
        /// <param name="newName"></param>
        /// <returns></returns>
        public async Task<bool> RenameFile(FSItem item, string newName)
        {
            if (!Utility.IsValidPathname(newName))
            {
                return false; // TODO: throw an exception
            }

            // 1. Is newName a folder?
            // var newParentNode = await FetchNode(newName);
            // if (newParentNode != null)
            // {
            //    return await MoveFile(item, newParentNode);
            // }

            // 2. Is newName a file in the same dir?
            var destination = Path.GetDirectoryName(newName);
            // 2.1 Is destination empty? (means that the file is in the current folder)
            if (destination == "")
            {
                CacheStorage.RemoveItems(item.Dir); // invalidate parent path
                await amazon.Nodes.Rename(item.Id, newName, item.IsDir);
                return true;
            }

            // 2.1.1 If destination (destination) node does not exist or is not a directory, we should fail
            var destinationNode = await FetchNode(destination);
            if (destinationNode == null || !destinationNode.IsDir)
            {
                return false; // TODO: throw exception
            }

            // 2.2 Is destination the same as the directory name of the item?
            var filename = Path.GetFileName(newName);
            if (filename == "")
            {
                filename = item.Name;
            }
            if (destination == item.Dir)
            {
                // cannot rename to myself
                if (filename == item.Name)
                {
                    return true;
                }
                CacheStorage.RemoveItems(item.Dir); // invalidate parent path
                await amazon.Nodes.Rename(item.Id, filename, item.IsDir);
                return true;
            }

            // 3. Is newName is another folder AND filename? (the only remaining option)
            //    Here we have 2 problems (because of no way to move and rename atomically):
            //    1) If we first rename and then move, then it might happen so that there is a file with the same name in the current folder
            //    2) Similar problem can be if first move and then rename
            //    Solution? We have it.
            //    1) We should first rename to something unique (say, filename.randomstr.ext) and most likely we will not get a conflict
            //    2) We move the file with this unique name
            //    3) We _try_ to rename back to the original name
            //    4) If we fail, we add (2), (3), (n) to the filename (i.e.: filename (n).ext)
            //    5) If we still fail, we at least have the same file with the randomstr in the filename
            var filenameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
            var extension = Path.GetExtension(filename);
            var randomString = Utility.RandomString(8);
            var tmpFilename = string.Format("{0}.{1}.{2}", filenameWithoutExtension, randomString, extension);

            // 3.1 Rename to a temporary name
            await amazon.Nodes.Rename(item.Id, tmpFilename, item.IsDir);
            // 3.2 Move to the new destination
            await amazon.Nodes.Move(item.Id, destinationNode.Id, item.IsDir);
            // 3.3 Rename back to the original name
            await amazon.Nodes.Rename(item.Id, filename, item.IsDir); // TODO: catch exceptions and try to rename again

            CacheStorage.RemoveItems(item.Dir); // invalidate parent path
            CacheStorage.RemoveItems(destinationNode.Path); // invalidate new parent path

            return true;
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="access_token"></param>
        public void OnTokenUpdated(string access_token)
        {
            var settings = ACDSettings.Default.GetData();
            settings.AuthToken = access_token;
            ACDSettings.Default.Save();
        }

        /// <summary>
        /// Get FarFile wrapper for FSItem
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public FarFile GetFarFileFromFSItem(FSItem item)
        {
            SetFile file = new SetFile()
            {
                Name = item.Name,
                Description = item.Id,
                IsDirectory = item.IsDir,
                LastAccessTime = item.LastAccessTime,
                LastWriteTime = item.LastWriteTime,
                Length = item.Length,
                CreationTime = item.CreationTime,
                Data = new Hashtable(),
            };
            //((Hashtable)file.Data).Add("fsitem", item);
            //CacheStorage.AddItem(item);

            return file;
        }
    }
}
