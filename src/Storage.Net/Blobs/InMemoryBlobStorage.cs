﻿using System.Collections.Generic;
using System.IO;
using System;
using NetBox.Model;
using System.Linq;
using NetBox.IO;
using System.Threading.Tasks;
using System.Threading;
using NetBox.Extensions;
using NetBox;
using Storage.Net.Streaming;

namespace Storage.Net.Blobs
{
   class InMemoryBlobStorage : IBlobStorage
   {
      struct Tag
      {
         public byte[] data;
         public DateTimeOffset lastMod;
         public string md5;
      }

      private readonly Dictionary<Blob, Tag> _idToData = new Dictionary<Blob, Tag>();

      public Task<IReadOnlyCollection<Blob>> ListAsync(ListOptions options, CancellationToken cancellationToken)
      {
         if (options == null) options = new ListOptions();

         options.FolderPath = StoragePath.Normalize(options.FolderPath);

         List<Blob> matches = _idToData

            .Where(e => options.Recurse
               ? e.Key.FolderPath.StartsWith(options.FolderPath)
               : StoragePath.ComparePath(e.Key.FolderPath, options.FolderPath))

            .Select(e => e.Key)
            .Where(options.IsMatch)
            .Where(e => options.BrowseFilter == null || options.BrowseFilter(e))
            .Take(options.MaxResults == null ? int.MaxValue : options.MaxResults.Value)
            .ToList();

         return Task.FromResult((IReadOnlyCollection<Blob>)matches);
      }

      public Task WriteAsync(Blob blob, Stream sourceStream, bool append, CancellationToken cancellationToken)
      {
         GenericValidation.CheckBlobFullPath(blob);
         string fullPath = StoragePath.Normalize(blob);

         if (append)
         {
            if (!Exists(fullPath))
            {
               Write(fullPath, sourceStream);
            }
            else
            {
               Tag tag = _idToData[fullPath];
               byte[] data = tag.data.Concat(sourceStream.ToByteArray()).ToArray();

               _idToData[fullPath] = ToTag(data);
            }
         }
         else
         {
            Write(fullPath, sourceStream);
         }

         return Task.FromResult(true);
      }

      public Task<Stream> OpenWriteAsync(Blob blob, bool append, CancellationToken cancellationToken)
      {
         GenericValidation.CheckBlobFullPath(blob);
         string fullPath = StoragePath.Normalize(blob);

         var result = new FixedStream(new MemoryStream(), null, async fx =>
         {
            MemoryStream ms = (MemoryStream)fx.Parent;
            ms.Position = 0;
            WriteAsync(fullPath, ms, append, cancellationToken).Wait();
         });

         return Task.FromResult<Stream>(result);
      }

      public Task<Stream> OpenReadAsync(string id, CancellationToken cancellationToken)
      {
         GenericValidation.CheckBlobFullPath(id);
         id = StoragePath.Normalize(id);

         if (!_idToData.TryGetValue(id, out Tag tag)) return Task.FromResult<Stream>(null);

         return Task.FromResult<Stream>(new NonCloseableStream(new MemoryStream(tag.data)));
      }

      public Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
      {
         GenericValidation.CheckBlobFullPaths(ids);

         foreach (string blobId in ids)
         {
            _idToData.Remove(StoragePath.Normalize(blobId));
         }

         return Task.FromResult(true);
      }

      public Task<IReadOnlyCollection<bool>> ExistsAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
      {
         var result = new List<bool>();

         foreach (string id in ids)
         {
            result.Add(_idToData.ContainsKey(StoragePath.Normalize(id)));
         }

         return Task.FromResult<IReadOnlyCollection<bool>>(result);
      }

      public Task<IReadOnlyCollection<Blob>> GetBlobsAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
      {
         GenericValidation.CheckBlobFullPaths(ids);

         var result = new List<Blob>();

         foreach (string id in ids)
         {
            if (!_idToData.TryGetValue(StoragePath.Normalize(id), out Tag tag))
            {
               result.Add(null);
            }
            else
            {
               var r = new Blob(id)
               {
                  Size = tag.data.Length,
                  MD5 = tag.md5,
                  LastModificationTime = tag.lastMod
               };

               result.Add(r);
            }
         }

         return Task.FromResult<IReadOnlyCollection<Blob>>(result);
      }

      private void Write(string id, Stream sourceStream)
      {
         GenericValidation.CheckBlobFullPath(id);
         id = StoragePath.Normalize(id);

         Tag tag = ToTag(sourceStream);

         _idToData[id] = tag;
      }

      private static Tag ToTag(Stream s)
      {
         if (s is MemoryStream ms) ms.Position = 0;
         return ToTag(s.ToByteArray());
      }

      private static Tag ToTag(byte[] data)
      {
         var tag = new Tag();
         tag.data = data;
         tag.lastMod = DateTimeOffset.UtcNow;
         tag.md5 = tag.data.GetHash(HashType.Md5).ToHexString();
         return tag;
      }

      private bool Exists(string id)
      {
         GenericValidation.CheckBlobFullPath(id);

         return _idToData.ContainsKey(id);
      }

      public void Dispose()
      {
      }

      public Task<ITransaction> OpenTransactionAsync()
      {
         return Task.FromResult(EmptyTransaction.Instance);
      }
   }
}
