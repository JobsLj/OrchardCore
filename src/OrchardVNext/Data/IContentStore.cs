using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace OrchardVNext.Data {
    public interface IContentStore : IDependency {
        Task<TDocument> GetAsync<TDocument>(int id) where TDocument : StorageDocument;
        Task<int> StoreAsync<TDocument>(TDocument document) where TDocument : StorageDocument;
        Task<IEnumerable<TDocument>> Query<TDocument>(Expression<Func<TDocument, bool>> map) where TDocument : StorageDocument;
        Task<IEnumerable<TDocument>> GetManyAsync<TDocument>(IEnumerable<int> ids) where TDocument : StorageDocument;
        Task RemoveAsync<TDocument>(int id) where TDocument : StorageDocument;
    }
}