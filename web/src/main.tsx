import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClientProvider } from '@tanstack/react-query';
import { AppBoot } from './app/AppBoot';
import { queryClient } from './app/queryClient';
import { initTheme } from './shared/ui/theme';
import './styles/theme.css';

// Before React renders, not in an effect. Applying the stored theme after the
// first paint would flash the OS theme - a white flash on every load for
// anyone whose OS is light, which is precisely who wants dark mode.
initTheme();

const rootElement = document.getElementById('root');
if (!rootElement) throw new Error('Missing #root element');

createRoot(rootElement).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <AppBoot />
    </QueryClientProvider>
  </StrictMode>,
);
