export function SiteHeader() {
  return (
    <header className="site-header">
      <a className="site-brand" href="#top" aria-label="DCR Bot">
        <span className="site-brand__mark">✦</span>
        <span>
          <strong>DCR Bot</strong>
          <small>Digital rådgivning</small>
        </span>
      </a>
      <nav className="site-nav" aria-label="Hovedmenu">
        <a href="#services">Selvbetjening</a>
        <a href="#guides">Guides</a>
        <a href="#about">Om DCR Bot</a>
      </nav>
      <button className="site-login" type="button">
        Log ind
      </button>
    </header>
  );
}
