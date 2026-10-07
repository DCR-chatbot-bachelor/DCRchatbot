export function FeatureSection() {
  return (
    <section className="feature-section" id="services">
      <div>
        <p className="eyebrow">Én samlet indgang</p>
        <h2>Det skal være nemt at komme videre.</h2>
      </div>
      <div className="feature-grid">
        <article>
          <span className="feature-icon">⌁</span>
          <h3>Find svar</h3>
          <p>Få klar og forståelig information, når du har brug for den.</p>
        </article>
        <article>
          <span className="feature-icon">✓</span>
          <h3>Bliv guidet</h3>
          <p>Gå gennem processen i dit eget tempo med hjælp undervejs.</p>
        </article>
        <article>
          <span className="feature-icon">↗</span>
          <h3>Kom videre</h3>
          <p>
            Få overblik over dine næste skridt og de oplysninger, du mangler.
          </p>
        </article>
      </div>
    </section>
  );
}
