// First: every /api/ request must carry the peng# format header (see pengFormat.ts).
import './api/pengFormat'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App, { EmbeddedPanel } from './App.tsx'

// Embed mode (?embed=1): render only the bird/box panel for a host WebView (the nestcheck
// modal), skipping login + full sync. Everything else boots the normal app.
const isEmbed = new URLSearchParams(window.location.search).get('embed') === '1'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {isEmbed ? <EmbeddedPanel /> : <App />}
  </StrictMode>,
)
