namespace Dudu.App.Overlay;

/// <summary>The cute things she can do with Dudu, from a click on Dudu's
/// body (<see cref="Pet"/>) or the Home page / settings header buttons.
/// Routed through <see cref="OverlayCommandRouter"/>.</summary>
public enum OverlayAction { Pet, DrinkWater, EatTogether, StudyTogether, TinyHug, BreatheWithMe }

/// <summary>Choices while the breathing exercise runs on Home: start it
/// again, or stop it (<see cref="Close"/>).</summary>
public enum ComfortAction { BreatheWithMe, Close }
