﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Utilities;
using EQueue.Utils;

namespace EQueue.Broker.Storage
{
    public unsafe class TFChunk : IDisposable
    {
        public const int WriteBufferSize = 8192;
        public const int ReadBufferSize = 8192;

        #region Private Variables

        private static readonly ILogger _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(typeof(TFChunk));

        private ChunkHeader _chunkHeader;
        private ChunkFooter _chunkFooter;

        private readonly string _filename;
        private readonly TFChunkManagerConfig _chunkConfig;
        private readonly bool _isMemoryChunk;
        private readonly ConcurrentQueue<ReaderWorkItem> _readerWorkItemQueue = new ConcurrentQueue<ReaderWorkItem>();

        private readonly object _writeSyncObj = new object();
        private readonly object _cacheSyncObj = new object();

        private IntPtr _cachedData;
        private int _cachedLength;

        private int _dataPosition;
        private volatile bool _isCompleted;
        private volatile bool _isDeleting;
        private int _cachingChunk;
        private DateTime _lastActiveTime;

        private TFChunk _memoryChunk;

        private WriterWorkItem _writerWorkItem;

        #endregion

        #region Public Properties

        public string FileName { get { return _filename; } }
        public ChunkHeader ChunkHeader { get { return _chunkHeader; } }
        public ChunkFooter ChunkFooter { get { return _chunkFooter; } }
        public TFChunkManagerConfig Config { get { return _chunkConfig; } }
        public bool IsCompleted { get { return _isCompleted; } }
        public DateTime LastActiveTime
        {
            get
            {
                var lastActiveTimeOfMemoryChunk = DateTime.MinValue;
                if (_memoryChunk != null)
                {
                    lastActiveTimeOfMemoryChunk = _memoryChunk.LastActiveTime;
                }
                return lastActiveTimeOfMemoryChunk >= _lastActiveTime ? lastActiveTimeOfMemoryChunk : _lastActiveTime;
            }
        }
        public bool IsMemoryChunk { get { return _isMemoryChunk; } }
        public bool HasCachedChunk { get { return _memoryChunk != null; } }
        public int DataPosition { get { return _dataPosition; } }
        public long GlobalDataPosition
        {
            get
            {
                return ChunkHeader.ChunkDataStartPosition + DataPosition;
            }
        }
        public bool IsFixedDataSize()
        {
            return _chunkConfig.ChunkDataUnitSize > 0 && _chunkConfig.ChunkDataCount > 0;
        }

        #endregion

        #region Constructors

        private TFChunk(string filename, TFChunkManagerConfig chunkConfig, bool isMemoryChunk)
        {
            Ensure.NotNullOrEmpty(filename, "filename");
            Ensure.NotNull(chunkConfig, "chunkConfig");

            _filename = filename;
            _chunkConfig = chunkConfig;
            _isMemoryChunk = isMemoryChunk;
            _lastActiveTime = DateTime.Now;
        }

        #endregion

        #region Factory Methods

        public static TFChunk CreateNew(string filename, int chunkNumber, TFChunkManagerConfig config, bool isMemoryChunk = false)
        {
            var chunk = new TFChunk(filename, config, isMemoryChunk);

            try
            {
                chunk.InitNew(chunkNumber);
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Chunk {0} create failed.", chunk), ex);
                chunk.Dispose();
                throw;
            }

            return chunk;
        }
        public static TFChunk FromCompletedFile(string filename, TFChunkManagerConfig config, bool isMemoryChunk = false)
        {
            var chunk = new TFChunk(filename, config, isMemoryChunk);

            try
            {
                chunk.InitCompleted();
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Chunk {0} init from completed file failed.", chunk), ex);
                chunk.Dispose();
                throw;
            }

            return chunk;
        }
        public static TFChunk FromOngoingFile<T>(string filename, TFChunkManagerConfig config, Func<int, BinaryReader, T> readRecordFunc, bool isMemoryChunk = false) where T : ILogRecord 
        {
            var chunk = new TFChunk(filename, config, isMemoryChunk);

            try
            {
                chunk.InitOngoing(readRecordFunc);
            }
            catch (Exception ex)
            {
                _logger.Error(string.Format("Chunk {0} init from ongoing file failed.", chunk), ex);
                chunk.Dispose();
                throw;
            }

            return chunk;
        }

        #endregion

        #region Init Methods

        private void InitCompleted()
        {
            var fileInfo = new FileInfo(_filename);
            if (!fileInfo.Exists)
            {
                throw new CorruptDatabaseException(new ChunkFileNotExistException(_filename));
            }

            _isCompleted = true;

            using (var fileStream = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadBufferSize, FileOptions.RandomAccess))
            {
                using (var reader = new BinaryReader(fileStream))
                {
                    _chunkHeader = ReadHeader(fileStream, reader);
                    _chunkFooter = ReadFooter(fileStream, reader);

                    CheckCompletedFileChunk();
                }
            }

            _dataPosition = _chunkFooter.ChunkDataTotalSize;

            SetFileAttributes();

            if (_isMemoryChunk)
            {
                LoadFileChunkToMemory();
            }

            InitializeReaderWorkItems();
        }
        private void InitNew(int chunkNumber)
        {
            var chunkDataSize = 0;
            if (_chunkConfig.ChunkDataSize > 0)
            {
                chunkDataSize = _chunkConfig.ChunkDataSize;
            }
            else
            {
                chunkDataSize = _chunkConfig.ChunkDataUnitSize * _chunkConfig.ChunkDataCount;
            }

            _chunkHeader = new ChunkHeader(chunkNumber, chunkDataSize);

            _isCompleted = false;

            var fileSize = ChunkHeader.Size + _chunkHeader.ChunkDataTotalSize + ChunkFooter.Size;

            var writeStream = default(Stream);

            if (_isMemoryChunk)
            {
                _cachedLength = fileSize;
                _cachedData = Marshal.AllocHGlobal(_cachedLength);
                writeStream = new UnmanagedMemoryStream((byte*)_cachedData, _cachedLength, _cachedLength, FileAccess.ReadWrite);
                writeStream.Write(_chunkHeader.AsByteArray(), 0, ChunkHeader.Size);
            }
            else
            {
                var tempFilename = string.Format("{0}.{1}.tmp", _filename, Guid.NewGuid());
                var tempFileStream = new FileStream(tempFilename, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, WriteBufferSize, FileOptions.SequentialScan);
                tempFileStream.SetLength(fileSize);
                tempFileStream.Write(_chunkHeader.AsByteArray(), 0, ChunkHeader.Size);
                tempFileStream.Flush(true);
                tempFileStream.Close();

                File.Move(tempFilename, _filename);

                writeStream = new FileStream(_filename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, WriteBufferSize, FileOptions.SequentialScan);
                SetFileAttributes();
            }

            writeStream.Position = ChunkHeader.Size;

            _dataPosition = 0;
            _writerWorkItem = new WriterWorkItem(writeStream);

            InitializeReaderWorkItems();

            if (!_isMemoryChunk)
            {
                _memoryChunk = TFChunk.CreateNew(_filename, chunkNumber, _chunkConfig, true);
                if (_logger.IsDebugEnabled)
                {
                    _logger.DebugFormat("Cached new chunk {0} to memory.", this);
                }
            }
        }
        private void InitOngoing<T>(Func<int, BinaryReader, T> readRecordFunc) where T : ILogRecord
        {
            var fileInfo = new FileInfo(_filename);
            if (!fileInfo.Exists)
            {
                throw new CorruptDatabaseException(new ChunkFileNotExistException(_filename));
            }

            _isCompleted = false;

            using (var fileStream = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadBufferSize, FileOptions.SequentialScan))
            {
                using (var reader = new BinaryReader(fileStream))
                {
                    _chunkHeader = ReadHeader(fileStream, reader);
                    SetStreamWriteStartPosition(fileStream, reader, readRecordFunc);
                    _dataPosition = (int)fileStream.Position - ChunkHeader.Size;
                }
            }

            var writeStream = default(Stream);

            if (_isMemoryChunk)
            {
                var fileSize = ChunkHeader.Size + _chunkHeader.ChunkDataTotalSize + ChunkFooter.Size;
                _cachedLength = fileSize;
                _cachedData = Marshal.AllocHGlobal(_cachedLength);
                writeStream = new UnmanagedMemoryStream((byte*)_cachedData, _cachedLength, _cachedLength, FileAccess.ReadWrite);

                writeStream.Write(_chunkHeader.AsByteArray(), 0, ChunkHeader.Size);

                if (_dataPosition > 0)
                {
                    using (var fileStream = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.SequentialScan))
                    {
                        fileStream.Seek(ChunkHeader.Size, SeekOrigin.Begin);
                        var buffer = new byte[65536];
                        int toReadBytes = _dataPosition;

                        while (toReadBytes > 0)
                        {
                            int read = fileStream.Read(buffer, 0, Math.Min(toReadBytes, buffer.Length));
                            if (read == 0)
                            {
                                break;
                            }
                            toReadBytes -= read;
                            writeStream.Write(buffer, 0, read);
                        }
                    }
                }

                if (writeStream.Position != _dataPosition + ChunkHeader.Size)
                {
                    throw new InvalidOperationException(string.Format("UnmanagedMemoryStream position incorrect, expect: {0}, but: {1}", _dataPosition + ChunkHeader.Size, writeStream.Position));
                }
            }
            else
            {
                writeStream = new FileStream(_filename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, WriteBufferSize, FileOptions.SequentialScan);
                writeStream.Position = _dataPosition + ChunkHeader.Size;
                SetFileAttributes();
            }

            _writerWorkItem = new WriterWorkItem(writeStream);

            InitializeReaderWorkItems();

            if (!_isMemoryChunk)
            {
                _memoryChunk = TFChunk.FromOngoingFile<T>(_filename, _chunkConfig, readRecordFunc, true);
                if (_logger.IsDebugEnabled)
                {
                    _logger.DebugFormat("Cached ongoing chunk {0} to memory.", this);
                }
            }
        }

        #endregion

        #region Public Methods

        public void TryCacheInMemory()
        {
            lock (_cacheSyncObj)
            {
                if (_isMemoryChunk || !_isCompleted || _memoryChunk != null)
                {
                    _logger.ErrorFormat("Cache completed chunk failed, _isMemoryChunk: {0}, _isCompleted: {1}, _memoryChunk is null: {2}", _isMemoryChunk, _isCompleted, _memoryChunk == null);
                    _cachingChunk = 0;
                    return;
                }

                try
                {
                    var physicalMemorySizeMB = MemoryInfoUtil.GetTotalPhysicalMemorySize();
                    var usedMemoryPercent = MemoryInfoUtil.GetUsedMemoryPercent();
                    var usedMemorySizeMB = physicalMemorySizeMB * usedMemoryPercent / 100;
                    var chunkSizeMB = (ChunkHeader.Size + _chunkHeader.ChunkDataTotalSize + ChunkFooter.Size) / 1024 / 1024;
                    var maxAllowUseMemoryPercent = _chunkConfig.MessageChunkCacheMaxPercent;
                    var maxAllowUseMemorySizeMB = physicalMemorySizeMB * maxAllowUseMemoryPercent / 100;

                    if (_chunkConfig.ForceCacheChunk || usedMemorySizeMB + chunkSizeMB <= maxAllowUseMemorySizeMB)
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.DebugFormat("Caching completed chunk {0} to memory, physicalMemorySize: {1}MB, currentUsedMemorySize: {2}MB, currentChunkSize: {3}MB, usedMemoryPercent: {4}%, maxAllowUseMemoryPercent: {5}%, isForceCache: {6}",
                                this,
                                physicalMemorySizeMB,
                                usedMemorySizeMB,
                                chunkSizeMB,
                                usedMemoryPercent,
                                maxAllowUseMemoryPercent,
                                _chunkConfig.ForceCacheChunk);
                        }
                        _memoryChunk = TFChunk.FromCompletedFile(_filename, _chunkConfig, true);
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.DebugFormat("Cached completed chunk {0} to memory.", this);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(string.Format("Cache completed chunk {0} to memory failed.", this), ex);
                }
                finally
                {
                    _cachingChunk = 0;
                }
            }
        }
        public void UnCacheFromMemory()
        {
            lock (_cacheSyncObj)
            {
                if (_isMemoryChunk || !_isCompleted || _memoryChunk == null)
                {
                    _logger.ErrorFormat("UnCache completed chunk failed, _isMemoryChunk: {0}, _isCompleted: {1}, _memoryChunk is null: {2}", _isMemoryChunk, _isCompleted, _memoryChunk == null);
                    return;
                }

                try
                {
                    var memoryChunk = _memoryChunk;
                    _memoryChunk = null;
                    memoryChunk.Dispose();
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.DebugFormat("Uncached completed chunk {0} from memory.", this);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(string.Format("Uncache completed chunk {0} from memory failed.", this), ex);
                }
            }
        }
        public T TryReadAt<T>(long dataPosition, Func<int, BinaryReader, T> readRecordFunc) where T : ILogRecord
        {
            if (_memoryChunk != null)
            {
                return _memoryChunk.TryReadAt<T>(dataPosition, readRecordFunc);
            }

            if (_isDeleting)
            {
                throw new InvalidReadException(string.Format("Chunk {0} is being deleting.", this));
            }

            if (!_isMemoryChunk && _isCompleted && Interlocked.CompareExchange(ref _cachingChunk, 1, 0) == 0)
            {
                Task.Factory.StartNew(TryCacheInMemory);
            }

            var readerWorkItem = GetReaderWorkItem();
            try
            {
                var currentDataPosition = DataPosition;
                if (dataPosition >= currentDataPosition)
                {
                    throw new InvalidReadException(
                        string.Format("Cannot read record after the max data position, data position: {0}, max data position: {1}, chunk: {2}",
                                      dataPosition, currentDataPosition, this));
                }

                return IsFixedDataSize() ?
                    TryReadFixedSizeForwardInternal(readerWorkItem, dataPosition, readRecordFunc) :
                    TryReadForwardInternal(readerWorkItem, dataPosition, readRecordFunc);
            }
            finally
            {
                ReturnReaderWorkItem(readerWorkItem);
                _lastActiveTime = DateTime.Now;
            }
        }
        public RecordWriteResult TryAppend(ILogRecord record)
        {
            if (_isCompleted)
            {
                throw new ChunkWriteException(this.ToString(), string.Format("Cannot write to a read-only chunk, isMemoryChunk: {0}, _dataPosition: {1}", _isMemoryChunk, _dataPosition));
            }

            var writerWorkItem = _writerWorkItem;
            var bufferStream = writerWorkItem.BufferStream;
            var bufferWriter = writerWorkItem.BufferWriter;

            if (IsFixedDataSize())
            {
                if (writerWorkItem.WorkingStream.Position + _chunkConfig.ChunkDataUnitSize > ChunkHeader.Size + _chunkHeader.ChunkDataTotalSize)
                {
                    return RecordWriteResult.NotEnoughSpace();
                }
                bufferStream.Position = 0;
                record.WriteTo(GlobalDataPosition, bufferWriter);
                var recordLength = bufferStream.Length;
                if (recordLength != _chunkConfig.ChunkDataUnitSize)
                {
                    throw new ChunkWriteException(this.ToString(), string.Format("Invalid fixed data length, expected length {0}, but was {1}", _chunkConfig.ChunkDataUnitSize, recordLength));
                }
            }
            else
            {
                bufferStream.SetLength(4);
                bufferStream.Position = 4;
                record.WriteTo(GlobalDataPosition, bufferWriter);
                var recordLength = (int)bufferStream.Length - 4;
                bufferWriter.Write(recordLength); // write record length suffix
                bufferStream.Position = 0;
                bufferWriter.Write(recordLength); // write record length prefix

                if (recordLength > _chunkConfig.MaxLogRecordSize)
                {
                    throw new ChunkWriteException(this.ToString(),
                        string.Format("Log record at data position {0} has too large length: {1} bytes, while limit is {2} bytes",
                                      _dataPosition, recordLength, _chunkConfig.MaxLogRecordSize));
                }

                if (writerWorkItem.WorkingStream.Position + recordLength + 2 * sizeof(int) > ChunkHeader.Size + _chunkHeader.ChunkDataTotalSize)
                {
                    return RecordWriteResult.NotEnoughSpace();
                }
            }

            var writtenPosition = _dataPosition;
            var buffer = bufferStream.GetBuffer();

            lock (_writeSyncObj)
            {
                writerWorkItem.AppendData(buffer, 0, (int)bufferStream.Length);
            }

            _dataPosition = (int)writerWorkItem.WorkingStream.Position - ChunkHeader.Size;

            var position = ChunkHeader.ChunkDataStartPosition + writtenPosition;

            if (_memoryChunk != null)
            {
                var result = _memoryChunk.TryAppend(record);
                if (!result.Success)
                {
                    throw new ChunkWriteException(this.ToString(), "Append record to file chunk success, but append to memory chunk failed as memory space not enough, this should not be happened.");
                }
                else if (result.Position != position)
                {
                    throw new ChunkWriteException(this.ToString(), string.Format("Append record to file chunk success, but append to memory chunk failed, the position is not equal, memory position: {0}, file position: {1}.", result.Position, position));
                }
            }

            _lastActiveTime = DateTime.Now;
            return RecordWriteResult.Successful(position);
        }
        public void Flush()
        {
            if (_isMemoryChunk) return;

            if (_isCompleted)
            {
                throw new InvalidOperationException(string.Format("Cannot flush a read-only TFChunk: {0}", this));
            }

            lock (_writeSyncObj)
            {
                _writerWorkItem.FlushToDisk();
            }
        }
        public void Complete()
        {
            lock (_writeSyncObj)
            {
                if (_isCompleted) return;

                _chunkFooter = WriteFooter();
                Flush();
                _isCompleted = true;

                _writerWorkItem.Dispose();
                _writerWorkItem = null;

                if (!_isMemoryChunk)
                {
                    SetFileAttributes();
                    if (_memoryChunk != null)
                    {
                        _memoryChunk.Complete();
                    }
                }
            }
        }

        #endregion

        #region Clean Methods

        public void Dispose()
        {
            Close();
        }
        public void Close()
        {
            lock (_writeSyncObj)
            {
                if (!_isCompleted)
                {
                    Flush();
                }

                CloseAllReaderWorkItems();
                FreeCachedChunk();
            }
        }
        public void Delete()
        {
            if (_isMemoryChunk) return;

            //检查当前chunk是否已完成
            if (!_isCompleted)
            {
                throw new InvalidOperationException(string.Format("Not allowed to delete a incompleted chunk {0}", this));
            }

            //首先设置删除标记
            _isDeleting = true;

            //关闭所有的ReaderWorkItem
            CloseAllReaderWorkItems();

            //删除Chunk文件
            File.SetAttributes(_filename, FileAttributes.Normal);
            File.Delete(_filename);
        }

        #endregion

        #region Helper Methods

        private void CheckCompletedFileChunk()
        {
            using (var fileStream = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadBufferSize, FileOptions.RandomAccess))
            {
                //检查Chunk文件的实际大小是否正确
                var chunkFileSize = ChunkHeader.Size + _chunkFooter.ChunkDataTotalSize + ChunkFooter.Size;
                if (chunkFileSize != fileStream.Length)
                {
                    throw new CorruptDatabaseException(new BadChunkInDatabaseException(
                        string.Format("The size of chunk {0} should be equals with fileStream's length {1}, but instead it was {2}.",
                                        this,
                                        fileStream.Length,
                                        chunkFileSize)));
                }

                //如果Chunk中的数据是固定大小的，则还需要检查数据总数是否正确
                if (IsFixedDataSize())
                {
                    if (_chunkFooter.ChunkDataTotalSize != _chunkHeader.ChunkDataTotalSize)
                    {
                        throw new CorruptDatabaseException(new BadChunkInDatabaseException(
                            string.Format("The total data size of chunk {0} should be {1}, but instead it was {2}.",
                                            this,
                                            _chunkHeader.ChunkDataTotalSize,
                                            _chunkFooter.ChunkDataTotalSize)));
                    }
                }
            }
        }
        private void LoadFileChunkToMemory()
        {
            var watch = Stopwatch.StartNew();

            using (var fileStream = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.RandomAccess))
            {
                var cachedLength = (int)fileStream.Length;
                var cachedData = Marshal.AllocHGlobal(cachedLength);

                try
                {
                    using (var unmanagedStream = new UnmanagedMemoryStream((byte*)cachedData, cachedLength, cachedLength, FileAccess.ReadWrite))
                    {
                        fileStream.Seek(0, SeekOrigin.Begin);
                        var buffer = new byte[65536];
                        int toRead = cachedLength;
                        while (toRead > 0)
                        {
                            int read = fileStream.Read(buffer, 0, Math.Min(toRead, buffer.Length));
                            if (read == 0)
                            {
                                break;
                            }
                            toRead -= read;
                            unmanagedStream.Write(buffer, 0, read);
                        }
                    }
                }
                catch
                {
                    Marshal.FreeHGlobal(cachedData);
                    throw;
                }

                _cachedData = cachedData;
                _cachedLength = cachedLength;
            }
        }
        private void FreeCachedChunk()
        {
            if (_isMemoryChunk)
            {
                var cachedData = Interlocked.Exchange(ref _cachedData, IntPtr.Zero);
                if (cachedData != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(cachedData);
                }
            }
        }

        private void InitializeReaderWorkItems()
        {
            for (var i = 0; i < _chunkConfig.ChunkReaderCount; i++)
            {
                _readerWorkItemQueue.Enqueue(CreateReaderWorkItem());
            }
        }
        private void CloseAllReaderWorkItems()
        {
            var watch = Stopwatch.StartNew();
            var closedCount = 0;

            while (closedCount < _chunkConfig.ChunkReaderCount)
            {
                ReaderWorkItem readerWorkItem;
                while (_readerWorkItemQueue.TryDequeue(out readerWorkItem))
                {
                    readerWorkItem.Reader.Close();
                    closedCount++;
                }

                if (closedCount >= _chunkConfig.ChunkReaderCount)
                {
                    break;
                }

                Thread.Sleep(1000);

                if (watch.ElapsedMilliseconds > 30 * 1000)
                {
                    _logger.ErrorFormat("Close chunk reader work items timeout, expect close count: {0}, real close count: {1}", _chunkConfig.ChunkReaderCount, closedCount);
                    break;
                }
            }
        }
        private ReaderWorkItem CreateReaderWorkItem()
        {
            var stream = default(Stream);
            if (_isMemoryChunk)
            {
                stream = new UnmanagedMemoryStream((byte*)_cachedData, _cachedLength);
            }
            else
            {
                stream = new FileStream(_filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ReadBufferSize, FileOptions.RandomAccess);
            }
            return new ReaderWorkItem(stream, new BinaryReader(stream));
        }
        private ReaderWorkItem GetReaderWorkItem()
        {
            ReaderWorkItem readerWorkItem;
            while (!_readerWorkItemQueue.TryDequeue(out readerWorkItem))
            {
                Thread.Sleep(1);
            }
            return readerWorkItem;
        }
        private void ReturnReaderWorkItem(ReaderWorkItem readerWorkItem)
        {
            _readerWorkItemQueue.Enqueue(readerWorkItem);
        }

        private ChunkFooter WriteFooter()
        {
            var currentTotalDataSize = DataPosition;

            //如果是固定大小的数据，则检查总数据大小是否正确
            if (IsFixedDataSize())
            {
                if (currentTotalDataSize != _chunkHeader.ChunkDataTotalSize)
                {
                    throw new ChunkCompleteException(string.Format("Cannot write the chunk footer as the current total data size is incorrect. chunk: {0}, expectTotalDataSize: {1}, currentTotalDataSize: {2}",
                        this,
                        _chunkHeader.ChunkDataTotalSize,
                        currentTotalDataSize));
                }
            }

            var workItem = _writerWorkItem;
            var footer = new ChunkFooter(currentTotalDataSize);

            workItem.AppendData(footer.AsByteArray(), 0, ChunkFooter.Size);

            Flush(); // trying to prevent bug with resized file, but no data in it

            var oldStreamLength = workItem.WorkingStream.Length;
            var newStreamLength = ChunkHeader.Size + currentTotalDataSize + ChunkFooter.Size;

            if (newStreamLength != oldStreamLength)
            {
                workItem.ResizeStream(newStreamLength);
            }

            return footer;
        }
        private ChunkHeader ReadHeader(FileStream stream, BinaryReader reader)
        {
            if (stream.Length < ChunkHeader.Size)
            {
                throw new Exception(string.Format("Chunk file '{0}' is too short to even read ChunkHeader, its size is {1} bytes.", _filename, stream.Length));
            }
            stream.Seek(0, SeekOrigin.Begin);
            return ChunkHeader.FromStream(reader, stream);
        }
        private ChunkFooter ReadFooter(FileStream stream, BinaryReader reader)
        {
            if (stream.Length < ChunkFooter.Size)
            {
                throw new Exception(string.Format("Chunk file '{0}' is too short to even read ChunkFooter, its size is {1} bytes.", _filename, stream.Length));
            }
            stream.Seek(-ChunkFooter.Size, SeekOrigin.End);
            return ChunkFooter.FromStream(reader, stream);
        }

        private T TryReadForwardInternal<T>(ReaderWorkItem readerWorkItem, long dataPosition, Func<int, BinaryReader, T> readRecordFunc) where T : ILogRecord
        {
            var currentDataPosition = DataPosition;

            if (dataPosition + 2 * sizeof(int) > currentDataPosition)
            {
                throw new InvalidReadException(
                    string.Format("No enough space even for length prefix and suffix, data position: {0}, max data position: {1}, chunk: {2}",
                                  dataPosition, currentDataPosition, this));
            }

            readerWorkItem.Stream.Position = GetStreamPosition(dataPosition);

            var length = readerWorkItem.Reader.ReadInt32();
            if (length <= 0)
            {
                throw new InvalidReadException(
                    string.Format("Log record at data position {0} has non-positive length: {1} in chunk {2}",
                                  dataPosition, length, this));
            }
            if (length > _chunkConfig.MaxLogRecordSize)
            {
                throw new InvalidReadException(
                    string.Format("Log record at data position {0} has too large length: {1} bytes, while limit is {2} bytes, in chunk {3}",
                                  dataPosition, length, _chunkConfig.MaxLogRecordSize, this));
            }
            if (dataPosition + length + 2 * sizeof(int) > currentDataPosition)
            {
                throw new InvalidReadException(
                    string.Format("There is not enough space to read full record (length prefix: {0}), data position: {1}, max data position: {2}, chunk: {3}",
                                  length, dataPosition, currentDataPosition, this));
            }

            var record = readRecordFunc(length, readerWorkItem.Reader);
            if (record == null)
            {
                throw new InvalidReadException(
                    string.Format("Cannot read a record from data position {0}. Something is seriously wrong in chunk {1}.",
                                  dataPosition, this));
            }

            int suffixLength = readerWorkItem.Reader.ReadInt32();
            if (suffixLength != length)
            {
                throw new InvalidReadException(
                    string.Format("Prefix/suffix length inconsistency: prefix length({0}) != suffix length ({1}), data position: {2}. Something is seriously wrong in chunk {3}.",
                                  length, suffixLength, dataPosition, this));
            }

            return record;
        }
        private T TryReadFixedSizeForwardInternal<T>(ReaderWorkItem readerWorkItem, long dataPosition, Func<int, BinaryReader, T> readRecordFunc) where T : ILogRecord
        {
            var currentDataPosition = DataPosition;

            if (dataPosition + _chunkConfig.ChunkDataUnitSize > currentDataPosition)
            {
                throw new InvalidReadException(
                    string.Format("No enough space for fixed data record, data position: {0}, max data position: {1}, chunk: {2}",
                                  dataPosition, currentDataPosition, this));
            }

            var startStreamPosition = GetStreamPosition(dataPosition);
            readerWorkItem.Stream.Position = startStreamPosition;

            var record = readRecordFunc(_chunkConfig.ChunkDataUnitSize, readerWorkItem.Reader);
            if (record == null)
            {
                throw new InvalidReadException(
                        string.Format("Read fixed record from data position: {0} failed, max data position: {1}. Something is seriously wrong in chunk {2}",
                                      dataPosition, currentDataPosition, this));
            }

            var recordLength = readerWorkItem.Stream.Position - startStreamPosition;
            if (recordLength != _chunkConfig.ChunkDataUnitSize)
            {
                throw new InvalidReadException(
                        string.Format("Invalid fixed record length, expected length {0}, but was {1}, dataPosition: {2}. Something is seriously wrong in chunk {3}",
                                      _chunkConfig.ChunkDataUnitSize, recordLength, dataPosition, this));
            }

            return record;
        }

        private void SetStreamWriteStartPosition<T>(FileStream stream, BinaryReader reader, Func<int, BinaryReader, T> readRecordFunc) where T : ILogRecord
        {
            stream.Position = ChunkHeader.Size;

            var startStreamPosition = stream.Position;
            var maxStreamPosition = stream.Length - ChunkFooter.Size;
            var isFixedDataSize = IsFixedDataSize();

            while (stream.Position < maxStreamPosition)
            {
                var success = false;
                if (isFixedDataSize)
                {
                    success = TryReadFixedSizeRecord(stream, reader, maxStreamPosition, readRecordFunc);
                }
                else
                {
                    success = TryReadRecord(stream, reader, maxStreamPosition, readRecordFunc);
                }

                if (success)
                {
                    startStreamPosition = stream.Position;
                }
                else
                {
                    break;
                }
            }

            if (startStreamPosition != stream.Position)
            {
                stream.Position = startStreamPosition;
            }
        }
        private bool TryReadRecord<T>(FileStream stream, BinaryReader reader, long maxStreamPosition, Func<int, BinaryReader, T> readRecordFunc) where T : ILogRecord
        {
            try
            {
                var startStreamPosition = stream.Position;
                if (startStreamPosition + 2 * sizeof(int) > maxStreamPosition)
                {
                    return false;
                }

                var length = reader.ReadInt32();
                if (length <= 0 || length > _chunkConfig.MaxLogRecordSize)
                {
                    return false;
                }
                if (startStreamPosition + length + 2 * sizeof(int) > maxStreamPosition)
                {
                    return false;
                }

                var record = readRecordFunc(length, reader);
                if (record == null)
                {
                    return false;
                }

                int suffixLength = reader.ReadInt32();
                if (suffixLength != length)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
        private bool TryReadFixedSizeRecord<T>(FileStream stream, BinaryReader reader, long maxStreamPosition, Func<int, BinaryReader, T> readRecordFunc) where T : ILogRecord
        {
            try
            {
                var startStreamPosition = stream.Position;
                if (startStreamPosition + _chunkConfig.ChunkDataUnitSize > maxStreamPosition)
                {
                    return false;
                }

                var record = readRecordFunc(_chunkConfig.ChunkDataUnitSize, reader);
                if (record == null)
                {
                    return false;
                }

                var recordLength = stream.Position - startStreamPosition;
                if (recordLength != _chunkConfig.ChunkDataUnitSize)
                {
                    _logger.ErrorFormat("Invalid fixed data length, expected length {0}, but was {1}", _chunkConfig.ChunkDataUnitSize, recordLength);
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
        private static long GetStreamPosition(long dataPosition)
        {
            return ChunkHeader.Size + dataPosition;
        }

        private void SetFileAttributes()
        {
            Helper.EatException(() =>
            {
                if (_isCompleted)
                {
                    File.SetAttributes(_filename, FileAttributes.ReadOnly | FileAttributes.NotContentIndexed);
                }
                else
                {
                    File.SetAttributes(_filename, FileAttributes.NotContentIndexed);
                }
            });
        }

        #endregion

        public override string ToString()
        {
            return string.Format("#{0} ({1},{2}-{3},{4})", _chunkHeader.ChunkNumber, _filename, _chunkHeader.ChunkDataStartPosition, _chunkHeader.ChunkDataEndPosition, _isMemoryChunk);
        }
    }
}