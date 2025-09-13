using System;
using System.Collections.Generic;

namespace TimeGhost
{
    internal class TileImpostorUtils
    {
        internal struct SortableWeightEntry<T> where T : unmanaged
        {
            public T value;
            public int index;
        }

        internal struct SortableWeightComparer<T> : IComparer<SortableWeightEntry<T>> where T: unmanaged, IComparable<T>
        {
            public int Compare(SortableWeightEntry<T> x, SortableWeightEntry<T> y)
            {
                return -x.value.CompareTo(y.value);
            }
        }
        

    }
}