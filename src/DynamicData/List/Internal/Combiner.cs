﻿// Copyright (c) 2011-2019 Roland Pheasant. All rights reserved.
// Roland Pheasant licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData.Annotations;
using DynamicData.Cache.Internal;

namespace DynamicData.List.Internal
{
    internal sealed class Combiner<T>
    {
        private readonly object _locker = new object();
        private readonly ICollection<IObservable<IChangeSet<T>>> _source;
        private readonly CombineOperator _type;

        public Combiner([NotNull] ICollection<IObservable<IChangeSet<T>>> source, CombineOperator type)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _type = type;
        }

        public IObservable<IChangeSet<T>> Run()
        {
            return Observable.Create<IChangeSet<T>>(observer =>
            {
                var disposable = new CompositeDisposable();

                var resultList = new ChangeAwareListWithRefCounts<T>();

                lock (_locker)
                {
                    var sourceLists = Enumerable.Range(0, _source.Count)
                        .Select(_ => new ReferenceCountTracker<T>())
                        .ToList();

                    foreach (var pair in _source.Zip(sourceLists, (item, list) => new { Item = item, List = list }))
                    {
                        disposable.Add(pair.Item.Synchronize(_locker).Subscribe(changes =>
                        {
                            CloneSourceList(pair.List, changes);

                            var notifications = UpdateResultList(changes, sourceLists, resultList);
                            if (notifications.Count != 0)
                            {
                                observer.OnNext(notifications);
                            }
                        }));
                    }
                }

                return disposable;
            });
        }

        private static void CloneSourceList(ReferenceCountTracker<T> tracker, IChangeSet<T> changes)
        {
            foreach (var change in changes)
            {
                switch (change.Reason)
                {
                    case ListChangeReason.Add:
                        tracker.Add(change.Item.Current);
                        break;
                    case ListChangeReason.AddRange:
                        foreach (var t in change.Range)
                        {
                            tracker.Add(t);
                        }

                        break;
                    case ListChangeReason.Replace:
                        tracker.Remove(change.Item.Previous.Value);
                        tracker.Add(change.Item.Current);
                        break;
                    case ListChangeReason.Remove:
                        tracker.Remove(change.Item.Current);
                        break;
                    case ListChangeReason.RemoveRange:
                    case ListChangeReason.Clear:
                        foreach (var t in change.Range)
                        {
                            tracker.Remove(t);
                        }

                        break;
                }
            }
        }

        private IChangeSet<T> UpdateResultList(IChangeSet<T> changes, List<ReferenceCountTracker<T>> sourceLists, ChangeAwareListWithRefCounts<T> resultList)
        {
            //child caches have been updated before we reached this point.
            foreach (var change in changes.Flatten())
            {
                var item = change.Current;
                var isInResult = resultList.Contains(item);
                var shouldBeInResult = MatchesConstraint(sourceLists, item);

                if (shouldBeInResult)
                {
                    if (!isInResult)
                    {
                        resultList.Add(item);
                    }
                    else if (change.Reason == ListChangeReason.Refresh)
                    {
                        resultList.Refresh(change.Current);
                    }
                }
                else
                {
                    if (isInResult)
                    {
                        resultList.Remove(item);
                    }
                }
            }

            return resultList.CaptureChanges();
        }

        private bool MatchesConstraint(List<ReferenceCountTracker<T>> sourceLists, T item)
        {
            switch (_type)
            {
                case CombineOperator.And:
                    {
                        return sourceLists.All(s => s.Contains(item));
                    }

                case CombineOperator.Or:
                    {
                        return sourceLists.Any(s => s.Contains(item));
                    }

                case CombineOperator.Xor:
                    {
                        return sourceLists.Count(s => s.Contains(item)) == 1;
                    }

                case CombineOperator.Except:
                    {
                        var first = sourceLists[0].Contains(item);
                        var others = sourceLists.Skip(1).Any(s => s.Contains(item));
                        return first && !others;
                    }

                default:
                    throw new ArgumentOutOfRangeException(nameof(item));
            }
        }
    }
}
