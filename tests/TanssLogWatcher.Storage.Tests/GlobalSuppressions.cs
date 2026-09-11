using System.Diagnostics.CodeAnalysis;

// Testnamen sind hier ganze deutsche Saetze mit Unterstrichen statt Leerzeichen. Das ist
// Absicht: Der Testbericht soll lesbar machen, WAS zugesichert wird, ohne dass jemand den
// Quelltext aufschlagen muss - "Haengengebliebenes_Senden_faellt_nach_Ablauf_zurueck" sagt
// mehr als "RequeueStuckTest". Die Regel zielt auf oeffentliche Programmierschnittstellen;
// ein Testprojekt hat keine.
[assembly: SuppressMessage("Naming", "CA1707",
    Justification = "Testnamen sind deutsche Saetze, keine oeffentliche Schnittstelle.",
    Scope = "namespaceanddescendants",
    Target = "~N:TanssLogWatcher.Storage.Tests")]
