interface HeroSectionProps {
  onStartChat: () => void;
}

export function HeroSection({ onStartChat }: HeroSectionProps) {
  return (
    <section className="hero-section" id="top">
      <div className="hero-copy">
        <p className="eyebrow">Velkommen til DCR Bot</p>
        <h1>
          Din digitale vej
          <br />
          til <em>tryg</em> rådgivning.
        </h1>
        <p className="hero-text">
          Få hjælp til at finde den rigtige information og udfylde dine
          oplysninger. Vores digitale assistent guider dig trin for trin.
        </p>
        <div className="hero-actions">
          <button className="hero-button" type="button" onClick={onStartChat}>
            Start en samtale <span aria-hidden="true">↗</span>
          </button>
          <a href="#services" className="hero-link">
            Se selvbetjening <span>↓</span>
          </a>
        </div>
        <div className="trust-row">
          <span className="trust-avatar">✓</span>
          <span>Sikker og fortrolig vejledning</span>
          <span className="trust-divider" />
          <span>Tilgængelig døgnet rundt</span>
        </div>
      </div>
      <div className="hero-visual" aria-hidden="true">
        <div className="hero-orb hero-orb--large" />
        <div className="hero-orb hero-orb--small" />
        <div className="hero-card hero-card--top">
          <span>✦</span> Enkel vejledning
        </div>
        <div className="hero-card hero-card--bottom">
          <strong>24/7</strong>
          <span>Altid her for dig</span>
        </div>
        <div className="hero-person">✦</div>
      </div>
    </section>
  );
}
