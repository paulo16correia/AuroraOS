#!/usr/bin/env python3
"""The one leg that leaves the machine, tested without leaving it.

A real request to ElevenLabs would need a key, a network and somebody's quota, so what stands in
for it is a server on loopback that speaks the same protocol. That is enough to test everything
that can actually go wrong here: which voice was chosen, that the language reached the request,
that one channel became two, that audio arrives in pieces rather than all at once, that closing
the stream closes the connection, and that a refusal says why instead of yielding silence.
"""

import http.server
import json
import struct
import threading
import unittest

import voice_engines


class Servidor(http.server.BaseHTTPRequestHandler):
    """Answers like the streaming endpoint does: chunked PCM, one channel, 16 bits."""

    pedidos = []
    estado = 200
    pedacos = 4
    amostras_por_pedaco = 480
    atraso = None

    def log_message(self, *_):
        pass

    def do_POST(self):
        comprimento = int(self.headers.get("Content-Length") or 0)
        corpo = json.loads(self.rfile.read(comprimento) or b"{}")
        Servidor.pedidos.append({"path": self.path, "corpo": corpo,
                                 "chave": self.headers.get("xi-api-key")})

        if Servidor.estado != 200:
            self.send_response(Servidor.estado)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"detail":{"message":"quota exceeded"}}')
            return

        self.send_response(200)
        self.send_header("Content-Type", "audio/pcm")
        self.end_headers()

        for i in range(Servidor.pedacos):
            mono = struct.pack("<%dh" % Servidor.amostras_por_pedaco,
                               *([1000 + i] * Servidor.amostras_por_pedaco))
            self.wfile.write(mono)
            self.wfile.flush()

            if Servidor.atraso is not None:
                Servidor.atraso.wait(2.0)


class SemRuido(http.server.HTTPServer):
    """A server that does not print a stack trace when the client hangs up.

    Cancelling mid-sentence closes the connection while the server is still writing, which is the
    behaviour being tested — so it should not also look like a failure in the test output.
    """

    def handle_error(self, request, client_address):
        pass


class Falso:
    def __enter__(self):
        Servidor.pedidos = []
        Servidor.estado = 200
        self.servidor = SemRuido(("127.0.0.1", 0), Servidor)
        self.host = "http://127.0.0.1:%d" % self.servidor.server_port
        self.thread = threading.Thread(target=self.servidor.serve_forever, daemon=True)
        self.thread.start()
        return self

    def __exit__(self, *_):
        self.servidor.shutdown()
        self.servidor.server_close()


def falar(engine, texto="Olá.", base=None, **kw):
    return b"".join(voice_engines.synthesise_stream(engine, texto, "chave-de-teste",
                                                    base=base, **kw))


class QualVoz(unittest.TestCase):
    """The installer chooses, and may reasonably want one voice or one per language."""

    def test_one_id_is_used_whatever_the_language(self):
        self.assertEqual("abc", voice_engines.resolve_voice("abc", "en"))
        self.assertEqual("abc", voice_engines.resolve_voice("abc", "pt-PT"))
        self.assertEqual("abc", voice_engines.resolve_voice("  abc  ", None))

    def test_a_map_picks_by_language(self):
        setting = {"en": "ingles", "pt": "portugues", "default": "resto"}

        self.assertEqual("ingles", voice_engines.resolve_voice(setting, "en"))
        self.assertEqual("portugues", voice_engines.resolve_voice(setting, "pt"))
        self.assertEqual("resto", voice_engines.resolve_voice(setting, "nl"))

    def test_a_region_falls_back_to_its_language_but_not_the_other_way(self):
        self.assertEqual("portugues", voice_engines.resolve_voice({"pt": "portugues"}, "pt-PT"))
        # pt-BR would also find "pt", which is correct: the setting said pt with no region.
        self.assertEqual("portugues", voice_engines.resolve_voice({"pt": "portugues"}, "pt-BR"))
        # but a setting that named the region is not found by the bare language
        self.assertIsNone(voice_engines.resolve_voice({"pt-PT": "europeu"}, "pt"))

    def test_nothing_configured_is_no_voice_rather_than_a_guess(self):
        self.assertIsNone(voice_engines.resolve_voice(None, "en"))
        self.assertIsNone(voice_engines.resolve_voice("", "en"))
        self.assertIsNone(voice_engines.resolve_voice({}, "en"))
        self.assertIsNone(voice_engines.resolve_voice({"fr": "x"}, "en"))

    def test_both_halves_are_needed_to_speak(self):
        self.assertIsNone(voice_engines.cloud_tts("voz", None))
        self.assertIsNone(voice_engines.cloud_tts(None, "chave"))
        self.assertIsNotNone(voice_engines.cloud_tts("voz", "chave"))


class OPedido(unittest.TestCase):
    def test_the_voice_language_and_key_all_reach_the_request(self):
        with Falso() as falso:
            engine = voice_engines.cloud_tts({"pt": "voz-pt"}, "k", language="pt-PT")
            falar(engine, base=falso.host)

        pedido = Servidor.pedidos[-1]

        self.assertIn("/v1/text-to-speech/voz-pt/stream", pedido["path"])
        self.assertIn("output_format=pcm_48000", pedido["path"])
        self.assertEqual("chave-de-teste", pedido["chave"])
        self.assertEqual("pt-PT", pedido["corpo"]["language_code"])
        self.assertEqual(voice_engines.ELEVENLABS_MODEL, pedido["corpo"]["model_id"])

    def test_an_unknown_language_is_left_out_rather_than_sent_empty(self):
        with Falso() as falso:
            falar(voice_engines.cloud_tts("voz", "k"), base=falso.host)

        self.assertNotIn("language_code", Servidor.pedidos[-1]["corpo"])

    def test_nothing_to_say_never_reaches_the_network(self):
        with Falso() as falso:
            engine = voice_engines.cloud_tts("voz", "k")

            for texto in ("", "   "):
                with self.assertRaises(RuntimeError):
                    falar(engine, texto, base=falso.host)

        self.assertEqual([], Servidor.pedidos)


class OAudio(unittest.TestCase):
    def test_one_channel_becomes_two_with_the_samples_duplicated(self):
        mono = struct.pack("<4h", 1, 2, 3, 4)
        estereo = voice_engines._to_stereo(mono)

        self.assertEqual(len(estereo), len(mono) * 2)
        self.assertEqual(struct.unpack("<8h", estereo), (1, 1, 2, 2, 3, 3, 4, 4))

    def test_the_stream_carries_every_sample_exactly_once(self):
        with Falso() as falso:
            pcm = falar(voice_engines.cloud_tts("voz", "k"), base=falso.host)

        esperado = Servidor.pedacos * Servidor.amostras_por_pedaco
        self.assertEqual(len(pcm), esperado * 4)          # 2 canais x 2 bytes

    def test_audio_arrives_in_pieces_and_not_all_at_the_end(self):
        """The property that makes speaking start early: the first piece is usable at once.

        This needs a source that does not have everything ready, which the first version of this
        test did not have — the whole answer was smaller than one read, so of course it arrived
        whole. The gate holds the server after its first chunk, which is what a real service
        generating speech as it goes actually looks like.
        """
        portao = threading.Event()

        with Falso() as falso:
            Servidor.atraso = portao

            try:
                fluxo = voice_engines.synthesise_stream(
                    voice_engines.cloud_tts("voz", "k"), "Olá.", "chave-de-teste",
                    base=falso.host)
                primeiro = next(fluxo)

                # usable audio, while the rest is demonstrably still behind the gate
                self.assertGreater(len(primeiro), 0)
                self.assertLess(len(primeiro),
                                Servidor.pedacos * Servidor.amostras_por_pedaco * 4)
                self.assertFalse(portao.is_set())

                fluxo.close()
            finally:
                Servidor.atraso = None
                portao.set()

    def test_a_sample_split_across_pieces_is_not_mangled(self):
        """An odd trailing byte must wait for its other half, not be duplicated as one."""
        pedacos = [b"\x01", b"\x02\x03", b"\x04"]
        saida = b""
        pendente = b""

        # the same holding-back the generator does, exercised directly on the helper
        for pedaco in pedacos:
            pendente += pedaco
            inteiro = len(pendente) - len(pendente) % 2
            saida += voice_engines._to_stereo(pendente[:inteiro])
            pendente = pendente[inteiro:]

        # 0x0201 = 513 and 0x0403 = 1027, each duplicated across the two channels and in order.
        # Getting this wrong would interleave the samples instead — (513, 1027, 513, 1027) — which
        # is audible as a burst of noise rather than as speech.
        self.assertEqual(struct.unpack("<4h", saida), (513, 513, 1027, 1027))


class QuandoFalha(unittest.TestCase):
    def test_a_refusal_says_why_instead_of_returning_silence(self):
        with Falso() as falso:
            Servidor.estado = 401
            engine = voice_engines.cloud_tts("voz", "k")

            with self.assertRaises(RuntimeError) as erro:
                falar(engine, base=falso.host)

        self.assertIn("401", str(erro.exception))
        self.assertIn("quota", str(erro.exception))

    def test_an_unreachable_service_is_a_refusal_not_a_hang(self):
        engine = voice_engines.cloud_tts("voz", "k")

        with self.assertRaises(RuntimeError) as erro:
            falar(engine, base="127.0.0.1:1", timeout=2.0)

        self.assertIn("could not be reached", str(erro.exception))

    def test_an_answer_with_no_audio_is_a_refusal(self):
        with Falso() as falso:
            Servidor.pedacos = 0

            try:
                with self.assertRaises(RuntimeError) as erro:
                    falar(voice_engines.cloud_tts("voz", "k"), base=falso.host)
            finally:
                Servidor.pedacos = 4

        self.assertIn("no audio", str(erro.exception))


if __name__ == "__main__":
    unittest.main(verbosity=2)
