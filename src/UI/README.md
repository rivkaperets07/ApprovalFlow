UI mínima para enviar facturas al `GatewayService`.

Cómo usar:

1. Abrir `src/UI/index.html` en un navegador (doble clic o servir estáticamente).
2. Ajustar el campo "Gateway URL" si tu `GatewayService` corre en otro puerto (p. ej. `http://localhost:5000`).
3. Rellenar los datos de la factura y pulsar "Enviar".

Notas:
- Esta UI es estática y pensada para pruebas locales. Para integrarla en Docker, puedo añadir un contenedor web estático (nginx) y actualizar `docker-compose.yml` si quieres.
- Los ejemplos de facturas están en `docs/sample-invoices.json`.
