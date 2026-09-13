using System.Diagnostics.CodeAnalysis;

// Testnamen sind hier ganze deutsche Saetze mit Unterstrichen statt Leerzeichen. Das ist
// Absicht: Der Testbericht soll lesbar machen, WAS zugesichert wird, ohne dass jemand den
// Quelltext aufschlagen muss - "Eine_Pause_zaehlt_nicht_zur_aufgezeichneten_Zeit" sagt mehr
// als "ClockPauseTest". Die Regel zielt auf oeffentliche Programmierschnittstellen; ein
// Testprojekt hat keine.
[assembly: SuppressMessage("Naming", "CA1707",
    Justification = "Testnamen sind deutsche Saetze, keine oeffentliche Schnittstelle.",
    Scope = "namespaceanddescendants",
    Target = "~N:TanssLogWatcher.Recording.Tests")]
