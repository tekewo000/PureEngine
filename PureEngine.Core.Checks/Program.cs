using PureEngine.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var scene = new Scene();
var first = scene.AddEmpty();
var second = scene.AddEmpty();
Check(first.Name == "Empty" && second.Name == "Empty (1)", "New names must not collide.");
Check(first.Id != second.Id, "Objects need stable, distinct identities.");
var originalId = first.Id;
var changes = 0;
first.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SceneObject.Name)) changes++; };
first.Rename("  Player  ");
first.Rename("Player");
Check(first.Name == "Player" && first.Id == originalId && changes == 1, "Rename must notify once without changing identity.");
try
{
    first.Rename("   ");
    throw new InvalidOperationException("Empty names must be rejected.");
}
catch (ArgumentException) { }
Check(first.Name == "Player", "Rejected edits must preserve the name.");
second.Rename("Player");
Check(scene.Remove(first) && scene.Objects.Count == 1 && scene.Objects[0] == second,
    "Deletion must use identity even when names match.");
Check(!scene.Remove(first), "Removing an absent object must not change the scene.");
Check(scene.AddEmpty().Name == "Empty", "Available default names should be reused.");
foreach (var item in scene.Objects.ToArray()) scene.Remove(item);
Check(scene.Objects.Count == 0, "The final object can be removed.");
Console.WriteLine("PASS: add, rename, validation, identity, notifications, and removal.");
