import "./globals.css";
import { DevSwitch } from "./dev-switch";

export const metadata = { title: "eisenberg" };

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body>
        <DevSwitch />
        {children}
      </body>
    </html>
  );
}
