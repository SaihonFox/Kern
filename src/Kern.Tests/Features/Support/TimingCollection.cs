using Xunit;

namespace Kern.Tests.Features.Support;

/// <summary>
/// Коллекция тестов с реальными таймерами/потоками: они не должны конкурировать друг
/// с другом за thread pool, иначе порядок `race`/`select` и ожидания становятся
/// недетерминированными. Остальные классы по-прежнему идут параллельно.
/// </summary>
[CollectionDefinition("timing", DisableParallelization = true)]
public sealed class TimingCollection;
