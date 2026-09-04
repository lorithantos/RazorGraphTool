using TopLevelApp;

// Every fact this fixture exists to prove is made from a global statement:
// a call, a guarded call, a throw, and a member access.
var nav = new Nav();
nav.Budget = 10;

var path = nav.FindPath(0, nav.Budget);
var total = nav.Accumulate(path);

try { nav.Risky(); }
catch (InvalidOperationException) { }

if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));

Console.WriteLine(total);
