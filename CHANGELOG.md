# Cambios

## 2.3.0

- Selector de perfil dibujado para la interfaz grafito/dorado: nombre, partidas y menú oscuro con teclado.
- Integración de LED-N 1.5.0: catálogo validado, colección, probabilidades, horas del tesoro, premios vistos y avisos de vehículos.
- Imágenes de colección incluidas en el ejecutable. Marcas manuales por perfil en el almacenamiento del navegador.
- Detección por proximidad de cofres destruidos al romper su soporte, movimiento/asientos de vehículos y corrección del callback de cierre de actores.
- Filtro opcional de BR normal de la temporada para partidas nuevas. Todas las partidas guardadas siguen visibles.
- Minimizar a bandeja opcional, recuperar ventana desde una segunda apertura, confirmación de cierre y ayuda para desinstalar conservando datos.
- Estimación opcional desde Carrera: descuenta partidas de otro PC y mantiene la estimación separada de las aperturas verificadas.
- OBS respeta el estilo HUD/clásico seleccionado.

La interfaz nativa propia, el cofre y Twitch se mantienen. No se adopta la ventana WebView2 del proyecto oficial: la interfaz personalizada ya cubre ventana, ajustes, informe y overlay sin añadir ese runtime. La desinstalación se documenta en Ayuda; esta edición portable no añade un instalador que pueda borrar el historial.

No se fuerza el reanálisis de partidas anteriores. La referencia local pasó de 77 a 79 cofres al recuperar dos destrucciones; el historial instalado conserva sus cifras. La atribución por proximidad y los avisos de vehículo tienen las limitaciones de lo grabado en el replay. Una partida nueva completa en Fortnite sigue siendo la validación pendiente del ciclo real.

Verificación: pruebas sintéticas y preservación de una copia real; ventana, selector y ajustes abiertos; ocho pestañas del informe a 1360 y 390 px, imágenes incluidas y persistencia de marcas. Las capturas públicas solo muestran datos ficticios.

## 2.2.0

Interfaz nativa propia inspirada en las pautas de unslop, cofre con transparencia, HUD y estilo clásico, y enlace exclusivo a Twitch.
