using System.Collections.Generic;
using System.Linq;

namespace Bullseye.Internal
{
    public static class EnumerableExtensions
    {
        public static IEnumerable<T> Sanitize<T>(this IEnumerable<T> items) where T : class =>
            items?.Where(item => item != null) ?? Enumerable.Empty<T>();
    }
}
