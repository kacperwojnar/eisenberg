import "./globals.css";
import { Shell } from "./shell";

export const metadata = { title: "eisenberg" };

export default function RootLayout({ children }: { children: React.ReactNode }) {
  const publicApi = process.env.PUBLIC_API_URL || "";
  return (
    <html lang="en" data-eisenberg="web">
      <head>
        {publicApi ? <meta name="eisenberg-api" content={publicApi} /> : null}
      </head>
      <body>
        <Shell>{children}</Shell>
      </body>
    </html>
  );
}
