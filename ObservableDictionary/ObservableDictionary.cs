using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Reactive.Collections
{
    public class ObservableDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, IObservable<TValue>>, IObservable<KeyValuePair<TKey, TValue>>, IDisposable
    {
        private readonly ConcurrentDictionary<TKey, Element> dictionary;
        private readonly Subject<IObservable<KeyValuePair<TKey, TValue>>> subject;
        private readonly IObservable<KeyValuePair<TKey, TValue>> notifier;
        private readonly TValue initial;
        private bool isDisposed;

        private struct Element : IDisposable
        {
            public CompositeDisposable Disposable { get; }
            public BehaviorSubject<TValue> Subject { get; }

            public Element(TValue value)
            {
                this.Disposable = new CompositeDisposable();
                this.Subject = new BehaviorSubject<TValue>(value);
            }

            public void Dispose()
            {
                this.Disposable.Dispose();
                this.Subject.OnCompleted();
                this.Subject.Dispose();
            }
        }

        public IObservable<TValue> this[TKey key]
        {
            get { return this.dictionary.GetOrAdd(key, x => CreateElement(x, this.initial)).Subject.AsObservable(); }
            set { SetSource(key, value, true); }
        }

        public void Add(TKey key, IObservable<TValue> source) => SetSource(key, source, false);

        public void Add(TKey key, TValue value)
        {
            Element element;
            while (!this.dictionary.TryGetValue(key, out element))
                if (this.dictionary.TryAdd(key, CreateElement(key, value)))
                    return;
            element.Subject.OnNext(value);
        }

        public bool Remove(TKey key)
        {
            Element element;
            if (!this.dictionary.TryRemove(key, out element))
                return false;
            element.Dispose();
            return true;
        }

        public bool TryGetValue(TKey key, out IObservable<TValue> value)
        {
            Element element;
            var result = this.dictionary.TryGetValue(key, out element);
            value = (result) ? element.Subject.AsObservable() : null;
            return result;
        }

        private void SetSource(TKey key, IObservable<TValue> source, bool reset)
        {
            var element = this.dictionary.GetOrAdd(key, x => CreateElement(x, this.initial));
            if (reset) element.Disposable.Clear();
            element.Disposable.Add(source.Subscribe(element.Subject.OnNext, ex => { }));
        }

        private Element CreateElement(TKey key, TValue value)
        {
            var element = new Element(value);
            this.subject.OnNext(element.Subject.Select(x => new KeyValuePair<TKey, TValue>(key, x)));
            return element;
        }

        public ObservableDictionary(TValue initial = default(TValue))
        {
            this.dictionary = new ConcurrentDictionary<TKey, Element>();
            this.subject = new Subject<IObservable<KeyValuePair<TKey, TValue>>>();
            this.notifier = Observable.Defer(() =>
                this.dictionary.Select(x => x.Value.Subject.Skip(1).Select(y => new KeyValuePair<TKey, TValue>(x.Key, y)))
                    .ToObservable(DefaultScheduler.Instance))
                .Merge(this.subject).Merge().Publish().RefCount();
            this.initial = initial;
            this.isDisposed = false;
        }

        public void Dispose()
        {
            if (this.isDisposed) return;
            this.isDisposed = true;

            foreach (var key in this.dictionary.Keys)
                this.Remove(key);
            this.subject.OnCompleted();
            this.subject.Dispose();
        }

        public IDisposable Subscribe(IObserver<KeyValuePair<TKey, TValue>> observer) => this.notifier.Subscribe(observer);

        public int Count => this.dictionary.Count;

        public IEnumerable<TKey> Keys => this.dictionary.Keys;

        public IEnumerable<IObservable<TValue>> Values => this.dictionary.Values.Select(x => x.Subject.AsObservable());

        public bool ContainsKey(TKey key) => this.dictionary.ContainsKey(key);

        public IEnumerator<KeyValuePair<TKey, IObservable<TValue>>> GetEnumerator() => this.dictionary
            .Select(x => new KeyValuePair<TKey, IObservable<TValue>>(x.Key, x.Value.Subject.AsObservable()))
            .GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
    }

    public static class ObservableDictionaryExtensions
    {
        public static ObservableDictionary<TKey, TValue> ToObservableDictionary<TKey, TValue>(this IDictionary<TKey, TValue> source, TValue initial = default(TValue)) =>
            source.ToMergeObservableDictionary(new ObservableDictionary<TKey, TValue>(initial));

        public static ObservableDictionary<TKey, TValue> ToObservableDictionary<TValue, TKey>(this IEnumerable<TValue> source, Func<TValue, TKey> keySelector, TValue initial = default(TValue)) =>
            source.ToDictionary(keySelector).ToObservableDictionary(initial);

        public static ObservableDictionary<TKey, TValue> ToObservableDictionary<TSource, TKey, TValue>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, Func<TSource, TValue> elementSelector, TValue initial = default(TValue)) =>
            source.ToDictionary(keySelector, elementSelector).ToObservableDictionary(initial);

        public static ObservableDictionary<TKey, TValue> ToUpdateObservableDictionary<TKey, TValue>(this IDictionary<TKey, TValue> source, ObservableDictionary<TKey, TValue> dictionary)
        {
            foreach (var key in dictionary.Keys)
                if (!source.ContainsKey(key)) dictionary.Remove(key);
            return source.ToMergeObservableDictionary(dictionary);
        }

        public static ObservableDictionary<TKey, TValue> ToUpdateObservableDictionary<TValue, TKey>(this IEnumerable<TValue> source, Func<TValue, TKey> keySelector, ObservableDictionary<TKey, TValue> dictionary) =>
            source.ToDictionary(keySelector).ToUpdateObservableDictionary(dictionary);

        public static ObservableDictionary<TKey, TValue> ToUpdateObservableDictionary<TSource, TKey, TValue>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, Func<TSource, TValue> elementSelector, ObservableDictionary<TKey, TValue> dictionary) =>
            source.ToDictionary(keySelector, elementSelector).ToUpdateObservableDictionary(dictionary);

        public static ObservableDictionary<TKey, TValue> ToMergeObservableDictionary<TKey, TValue>(this IDictionary<TKey, TValue> source, ObservableDictionary<TKey, TValue> dictionary)
        {
            foreach (var item in source)
                dictionary.Add(item.Key, item.Value);
            return dictionary;
        }

        public static ObservableDictionary<TKey, TValue> ToMergeObservableDictionary<TValue, TKey>(this IEnumerable<TValue> source, Func<TValue, TKey> keySelector, ObservableDictionary<TKey, TValue> dictionary) =>
            source.ToDictionary(keySelector).ToMergeObservableDictionary(dictionary);

        public static ObservableDictionary<TKey, TValue> ToMergeObservableDictionary<TSource, TKey, TValue>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, Func<TSource, TValue> elementSelector, ObservableDictionary<TKey, TValue> dictionary) =>
            source.ToDictionary(keySelector, elementSelector).ToMergeObservableDictionary(dictionary);
    }
}
