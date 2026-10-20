namespace Shapeshift.Engine.Servicing;

/// <summary>DISM не смог обслужить образ; сообщение — для пользователя.</summary>
public sealed class ServicingException(string message, Exception inner) : Exception(message, inner);
