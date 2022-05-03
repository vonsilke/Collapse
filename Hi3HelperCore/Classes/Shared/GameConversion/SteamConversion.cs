﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Threading;

using Newtonsoft.Json;
using Force.Crc32;

using Hi3Helper.Data;
using Hi3Helper.Preset;
using Hi3Helper.Shared.ClassStruct;
using static Hi3Helper.Data.ConverterTool;
using static Hi3Helper.Shared.Region.LauncherConfig;

namespace Hi3Helper.Shared.GameConversion
{
    public class SteamConversion
    {
        private string targetPath;
        private string endpointURL;
        private HttpClientHelper http;
        private Stream stream;
        private Stream bufferStream;
        private CancellationTokenSource tokenSource;
        private Stopwatch sw;

        private List<FilePropertiesRemote> BrokenFileIndexesProperty = new List<FilePropertiesRemote>();

        public event EventHandler<ConversionTaskChanged> ProgressChanged;

        private string CheckStatus;
        private long TotalSizeToRead, TotalRead;
        private int TotalCountToRead, TotalCount;
        private int DownloadThread = GetAppConfigValue("DownloadThread").ToInt();

        private string FilePath, FileCRC, FileURL;
        private FileInfo FileInfo;
        private FilePropertiesRemote FileIndex;
        private Crc32Algorithm FileCRCTool;
        private byte[] buffer = new byte[0x400000];

        public SteamConversion(string targetPath, string endpointURL, List<FilePropertiesRemote> FileList, CancellationTokenSource tokenSource)
        {
            this.sw = Stopwatch.StartNew();
            this.targetPath = targetPath;
            this.endpointURL = endpointURL;
            this.tokenSource = tokenSource;
            this.http = new HttpClientHelper();
            this.BrokenFileIndexesProperty = FileList;
        }

        public void StartConverting()
        {
            sw = Stopwatch.StartNew();

            TotalCount = 0;
            TotalRead = 0;
            TotalSizeToRead = BrokenFileIndexesProperty.Sum(x => x.S)
                            + BrokenFileIndexesProperty.Where(x => x.BlkC != null).Sum(x => x.BlkC.Sum(x => x.BlockSize));

            TotalCountToRead = BrokenFileIndexesProperty.Count
                             + BrokenFileIndexesProperty.Where(x => x.BlkC != null).Sum(y => y.BlkC.Sum(z => z.BlockContent.Count));

            foreach (var index in BrokenFileIndexesProperty)
            {
                FileIndex = index;
                FilePath = Path.Combine(targetPath, NormalizePath(index.N));
                switch (index.FT)
                {
                    case FileType.Generic:
                    case FileType.Audio:
                        ConvertGenericAudioFile();
                        break;
                    case FileType.Blocks:
                        ConvertBlockFile();
                        break;
                }
            }
        }

        private void ConvertGenericAudioFile()
        {
            TotalCount++;
            CheckStatus = $"Converting {FileIndex.FT} File [{TotalCount}/{TotalCountToRead}]: {FileIndex.N}";

            FileInfo = new FileInfo(FilePath);

            FileURL = endpointURL + 
                (FileIndex.FT == FileType.Generic ? FileIndex.N :
                                                    Path.GetDirectoryName(FileIndex.N).Replace('\\', '/') + $"/{FileIndex.RN}");

            http.DownloadProgress += HttpAdapter;

            if (FileIndex.S == 0)
                FileInfo.Create();
            else
                if (FileIndex.S > 10 << 20)
                http.DownloadFile(FileURL, FilePath, DownloadThread, tokenSource.Token);
            else
                using (stream = FileInfo.Create())
                    http.DownloadFile(FileURL, stream, tokenSource.Token, -1, -1, false);

            http.DownloadProgress -= HttpAdapter;
        }

        private void ConvertBlockFile()
        {
            string BlockBasePath = NormalizePath(FileIndex.N);
            long FileExistingLength;
            foreach (var block in FileIndex.BlkC)
            {
                FileURL = endpointURL + FileIndex.N + $"/{block.BlockHash}.wmv";
                FilePath = Path.Combine(targetPath, BlockBasePath, block.BlockHash + ".wmv");
                FileInfo = new FileInfo(FilePath);

                FileExistingLength = FileInfo.Exists ? FileInfo.Length : 0;

                if (FileInfo.Exists && FileInfo.Length != block.BlockSize)
                    FileInfo.Delete();

                http.DownloadProgress += HttpAdapter;
                if (!FileInfo.Exists)
                {
                    TotalCount++;
                    CheckStatus = $"Downloading {FileIndex.FT} [{TotalCount}/{TotalCountToRead}]: {block.BlockHash}";

                    using (stream = FileInfo.Create())
                        http.DownloadFile(FileURL, stream, tokenSource.Token, -1, -1, false);
                }
                else
                {
                    using (stream = FileInfo.OpenWrite())
                    {
                        foreach (var chunk in block.BlockContent)
                        {
                            TotalCount++;
                            CheckStatus = $"Downloading {FileIndex.FT} [{TotalCount}/{TotalCountToRead}]: {block.BlockHash} -> (0x{chunk._startoffset.ToString("x8")} | S: 0x{chunk._filesize.ToString("x8")})";

                            stream.Position = chunk._startoffset;

                            using (stream = FileInfo.Create())
                                http.DownloadFile(FileURL, stream, tokenSource.Token, chunk._startoffset, chunk._startoffset + chunk._filesize, false);
                        }
                    }
                }
                http.DownloadProgress -= HttpAdapter;
            }
        }

        private void HttpAdapter(object sender, HttpClientHelper._DownloadProgress e)
        {
            if (e.DownloadState == HttpClientHelper.State.Downloading)
                TotalRead += e.CurrentRead;

            OnProgressChanged(new ConversionTaskChanged(TotalRead, TotalSizeToRead, sw.Elapsed.TotalSeconds)
            {
                Message = $"{CheckStatus}"
            });
        }

        protected virtual void OnProgressChanged(ConversionTaskChanged e) => ProgressChanged?.Invoke(this, e);
    }

    public class ConversionTaskChanged : EventArgs
    {
        public ConversionTaskChanged(long totalReceived, long fileSize, double totalSecond)
        {
            BytesReceived = totalReceived;
            TotalBytesToReceive = fileSize;
            CurrentSpeed = (long)(totalReceived / totalSecond);
        }
        public string Message { get; set; }
        public long CurrentReceived { get; set; }
        public long BytesReceived { get; private set; }
        public long TotalBytesToReceive { get; private set; }
        public float ProgressPercentage => ((float)BytesReceived / (float)TotalBytesToReceive) * 100;
        public long CurrentSpeed { get; private set; }
        public TimeSpan TimeLeft => TimeSpan.FromSeconds((TotalBytesToReceive - BytesReceived) / CurrentSpeed);
    }
}
