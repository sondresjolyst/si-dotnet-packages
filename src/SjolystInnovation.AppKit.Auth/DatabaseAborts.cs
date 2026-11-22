using System.Data.Common;

namespace SjolystInnovation.AppKit.Auth;

internal static class DatabaseAborts
{
    // SQLSTATE 40001 is a serialization failure and 40P01 a deadlock. The database aborted the transaction, so the
    // change was not stored. Npgsql 11 reports both from SaveChanges as a concurrency exception around the database's
    // own error, so they look like a lost optimistic concurrency check unless the chain is read.
    internal static bool IsAbort(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is DbException { SqlState: "40001" or "40P01" })
                return true;
        }
        return false;
    }
}
