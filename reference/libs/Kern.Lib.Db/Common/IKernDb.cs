using Kern.Binding.Wrappers;

namespace Kern.Lib.Db.Common;

/// <summary>Общий контракт баз данных Kern (sqlite, postgres, mysql): им пользуется мини-ORM.</summary>
public interface IKernDb
{
	SqlDialect Dialect { get; }
	long LastId { get; }
	KResult<long, string> Exec(string sql, KList<object> args);
	KResult<KList<KDict<string, object>>, string> Query(string sql, KList<object> args);
	KResult<object?, string> Scalar(string sql, KList<object> args);
}
