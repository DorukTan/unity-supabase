using System;
using System.Collections;
using System.Threading.Tasks;

namespace Supabase.Unity
{
    public static class SupabaseTaskExtensions
    {
        public static IEnumerator AsCoroutine<T>(this Task<T> task, Action<T> completed,
            Action<Exception> failed = null)
        {
            if (task == null)
                throw new ArgumentNullException("task");
            while (!task.IsCompleted)
                yield return null;

            if (task.IsCanceled)
            {
                if (failed != null)
                    failed(new TaskCanceledException(task));
                yield break;
            }
            if (task.IsFaulted)
            {
                if (failed != null)
                    failed(task.Exception == null ? null : task.Exception.GetBaseException());
                yield break;
            }
            if (completed != null)
                completed(task.Result);
        }

        public static IEnumerator AsCoroutine(this Task task, Action completed = null,
            Action<Exception> failed = null)
        {
            if (task == null)
                throw new ArgumentNullException("task");
            while (!task.IsCompleted)
                yield return null;

            if (task.IsCanceled)
            {
                if (failed != null)
                    failed(new TaskCanceledException(task));
                yield break;
            }
            if (task.IsFaulted)
            {
                if (failed != null)
                    failed(task.Exception == null ? null : task.Exception.GetBaseException());
                yield break;
            }
            if (completed != null)
                completed();
        }
    }
}
