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
import os
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


class AVozEUmaSo(unittest.TestCase):
    """There is one speech client in Aurora, and this plugin does not get to have a second."""

    def test_the_vendored_client_is_the_voice_plugins_byte_for_byte(self):
        # A copy rather than an import, because plugins do not share a process, a sandbox or a
        # directory — each is granted its own and nothing outside it is readable. A copy nobody
        # checks is how two implementations come back, so it is checked here: edit
        # plugins/voice/speech.py and copy it over, and this passes; edit the copy and it does not.
        import os

        aqui = os.path.dirname(os.path.abspath(__file__))
        copia = os.path.join(aqui, "vendor", "aurora_voice", "speech.py")
        original = os.path.join(os.path.dirname(aqui), "voice", "speech.py")

        self.assertTrue(os.path.exists(copia), copia)
        self.assertTrue(os.path.exists(original),
                        "the voice plugin is the source of this file and it is not there: "
                        + original)

        with open(copia, "rb") as c, open(original, "rb") as o:
            self.assertEqual(
                o.read(), c.read(),
                "the vendored speech client has drifted from plugins/voice/speech.py — copy it "
                "over rather than editing the copy")

    def test_this_plugin_does_not_write_its_own_request_to_the_speech_service(self):
        # The duplication this replaced: the URL, the key header and the model were written out
        # again here, each free to disagree with the voice plugin's. Whatever else changes, the
        # request itself is built in one place.
        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                               "voice_engines.py"), encoding="utf-8") as h:
            fonte = h.read()

        self.assertNotIn("xi-api-key", fonte)
        self.assertNotIn("/v1/text-to-speech/", fonte)


class OQueEstaPronto(unittest.TestCase):
    """`readiness` is how somebody finds out what is missing before it fails mid-conversation.

    Nothing tested it, and it is called from six places. It broke the moment the speaking engine
    stopped being a dict and became the voice plugin's speaker object: it still indexed it, so
    asking what voice could do raised TypeError exactly when both halves were present — the one case
    where the answer is yes.
    """

    def test_it_names_the_voice_and_the_service_when_both_halves_are_there(self):
        ready = voice_engines.readiness(voice="vozinha", api_key="k", language="en")

        # Which voice, not merely that there is one: the setting may hold several and which one
        # answers depends on the language being spoken, so "tts: elevenlabs" says nothing useful.
        self.assertEqual("vozinha", ready["voice"])
        self.assertEqual("elevenlabs", ready["tts"])

        # Nothing about speaking is missing. Not `missing == []` and not `can_speak`, because both
        # also answer for libopus and a whisper model — native pieces that are present on one
        # machine and absent on the next, which would make this test report the machine rather than
        # the code.
        self.assertFalse(any("elevenlabs" in m or "tts_voice" in m for m in ready["missing"]))

    def test_which_half_is_missing_is_named_separately(self):
        # They are fixed in different places by different people: the voice is a setting somebody
        # edits, the key is a secret somebody types into a prompt. "Voice unavailable" would send
        # one of them hunting in the wrong file.
        sem_chave = voice_engines.readiness(voice="vozinha", api_key=None)
        sem_voz = voice_engines.readiness(voice=None, api_key="k")

        self.assertFalse(sem_chave["can_speak"])
        self.assertFalse(sem_voz["can_speak"])

        self.assertTrue(any("elevenlabs_api_key" in m for m in sem_chave["missing"]))
        self.assertFalse(any("elevenlabs_api_key" in m for m in sem_voz["missing"]))

        self.assertTrue(any("tts_voice" in m for m in sem_voz["missing"]))
        self.assertFalse(any("tts_voice" in m for m in sem_chave["missing"]))

    def test_it_says_which_half_of_voice_leaves_the_machine(self):
        # The two are no longer the same answer and would be the properties quietly lost first.
        com = voice_engines.readiness(voice="vozinha", api_key="k")
        sem = voice_engines.readiness(voice=None, api_key=None)

        self.assertFalse(com["audio_leaves_this_machine"])
        self.assertFalse(sem["audio_leaves_this_machine"])

        self.assertTrue(com["text_leaves_this_machine"])
        self.assertEqual(voice_engines.ELEVENLABS_HOST, com["speech_service"])

        # Nothing configured means nothing is sent anywhere, and it says so rather than naming a
        # service it cannot reach.
        self.assertFalse(sem["text_leaves_this_machine"])
        self.assertIsNone(sem["speech_service"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
