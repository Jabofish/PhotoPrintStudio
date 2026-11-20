using Microsoft.ML.OnnxRuntime;

namespace PhotoPrintStudio.Core.Imaging;

internal static class OnnxInference
{
    internal static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(
        InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs, CancellationToken token)
    {
        if (!token.CanBeCanceled) return session.Run(inputs);

        token.ThrowIfCancellationRequested();
        using var options = new RunOptions();
        using var registration = token.UnsafeRegister(static state => ((RunOptions)state!).Terminate = true, options);
        try
        {
            return session.Run(inputs, session.OutputNames, options);
        }
        catch (OnnxRuntimeException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }
}
