using System.Text;
using Shapeshift.Engine;

// Без этого перенаправленный вывод идёт в кодовой странице консоли (на русской Windows — cp866),
// и окно получило бы кириллицу искажённой.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

return await Cli.RunAsync(args, Console.Out, Console.Error);
