﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;

namespace Reactive.Collections
{
    public class ObservableDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, IObservable<TValue>>, IObservable<KeyValuePair<TKey, TValue>>, IDisposable
    {
        private readonly ConcurrentDictionary<TKey, Element> dictionary;
        private readonly Subject<IObservable<KeyValuePair<TKey, TValue>>> subject;
        private readonly IObservable<KeyValuePair<TKey, TValue>> notifier;
        private readonly Func<TKey, Element> initializer;
        private int isDisposed;

        private struct Element : IDisposable
        {
            public CompositeDisposable Disposable { get; }
            public BehaviorSubject<TValue> Subject { get; }
            public IObservable<TValue> Observable { get; }

            public Element(TValue value)
            {
                this.Disposable = new CompositeDisposable();
                this.Subject = new BehaviorSubject<TValue>(value);
                this.Observable = this.Subject.AsObservable();
            }

            public void Dispose()
            {
                this.Disposable.Dispose();
                this.Subject.OnCompleted();
            }
        }

        public IObservable<TValue> this[TKey key]
        {
            get { return this.dictionary.GetOrAdd(key, this.initializer).Observable; }
            set { SetSource(key, value, true); }
        }

        public void Add(TKey key, IObservable<TValue> source) => SetSource(key, source, false);

        public void Add(TKey key, TValue value)
        {
            Element element;
            while (!this.dictionary.TryGetValue(key, out element))
                if (this.dictionary.TryAdd(key, element = CreateElement(key, value)))
                    return;
                else
                    element.Dispose();
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
            value = (result) ? element.Observable : null;
            return result;
        }

        private void SetSource(TKey key, IObservable<TValue> source, bool reset)
        {
            var element = this.dictionary.GetOrAdd(key, this.initializer);
            if (reset) element.Disposable.Clear();
            element.Disposable.Add(source.Subscribe(element.Subject.OnNext, ex => { }));
        }

        private Element CreateElement(TKey key, TValue value)
        {
            if (this.IsDisposed) throw new ObjectDisposedException(string.Empty);
            var element = new Element(value);
            this.subject.OnNext(element.Subject.Select(x => new KeyValuePair<TKey, TValue>(key, x)));
            return element;
        }

        public ObservableDictionary(TValue initial = default(TValue))
        {
            this.dictionary = new ConcurrentDictionary<TKey, Element>();
            this.subject = new Subject<IObservable<KeyValuePair<TKey, TValue>>>();
            this.notifier = Observable
                .Defer(() => this.dictionary
                    .Select(item => item.Value.Subject.Skip(1).Select(x => new KeyValuePair<TKey, TValue>(item.Key, x)))
                    .ToObservable(DefaultScheduler.Instance))
                .Merge(this.subject).Merge().Publish().RefCount();
            this.initializer = key => CreateElement(key, initial);
            this.isDisposed = 0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.isDisposed, 1) != 0) return;
            do
                foreach (var key in this.dictionary.Keys)
                    this.Remove(key);
            while (!this.dictionary.IsEmpty);
            this.subject.OnCompleted();
        }

        public bool IsDisposed => this.isDisposed != 0;

        public IReadOnlyDictionary<TKey, TValue> GetCurrent() => this.dictionary.ToDictionary(x => x.Key, x => x.Value.Subject.Value);

        public TValue GetCurrent(TKey key) => this.dictionary[key].Subject.Value;

        public IDisposable Subscribe(IObserver<KeyValuePair<TKey, TValue>> observer) => this.notifier.Subscribe(observer);

        public int Count => this.dictionary.Count;

        public IEnumerable<TKey> Keys => this.dictionary.Keys;

        public IEnumerable<IObservable<TValue>> Values => this.dictionary.Values.Select(x => x.Observable);

        public bool ContainsKey(TKey key) => this.dictionary.ContainsKey(key);

        public IEnumerator<KeyValuePair<TKey, IObservable<TValue>>> GetEnumerator() => this.dictionary
            .Select(x => new KeyValuePair<TKey, IObservable<TValue>>(x.Key, x.Value.Observable))
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
