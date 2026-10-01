"""The capability layer: what Aurora asks of this plugin, and what comes back.

These exist because two defects got through without them. Both were references left behind when the
telephone was removed — `session.transport` in `voice.poll` and `interaction.FAILED` in
`voice.tool_result` — and neither was reachable from the tests that did exist. `test_local` covers
the conversation with the engines faked and never goes through a capability; the C# suite goes
through every capability and only ever down the path where nothing is missing.

So the gap was the whole of this file: the six functions Aurora actually calls. The interaction
layer is faked here, because what is being tested is the handing over and not the conversation.
"""
import io
import json
import unittest

import voice_service


class Fingida:
    """An interaction layer that records rather than converses, and reports nothing spent.

    Reporting what a conversation cost is optional — the capability asks whether the layer offers
    it — so the base here does not, and `ComTelemetria` does.
    """

    def __init__(self, eventos=None):
        self.eventos = list(eventos or [])
        self.ouvido = []
        self.entregue = []
        self.interrompido = 0
        self.fechado = None
        self.comecado = False

    def start(self):
        self.comecado = True

    def poll(self):
        saida, self.eventos = self.eventos, []
        return saida

    def append_audio(self, audio):
        self.ouvido.append(audio)

    def deliver(self, request_id, outcome):
        self.entregue.append((request_id, outcome))

    def interrupt(self):
        self.interrompido += 1

    def close(self, reason):
        self.fechado = reason


class ComTelemetria(Fingida):
    """An interaction layer that counts what the conversation spent."""

    def __init__(self, telemetria, **k):
        super().__init__(**k)
        self.telemetria = telemetria

    def telemetry(self):
        return self.telemetria


class Caso(unittest.TestCase):
    def setUp(self):
        # Reports go to stdout as protocol frames. Captured rather than printed, and read back,
        # because what the plugin reports is part of its contract.
        self.dito = io.StringIO()
        self._say = voice_service.say
        voice_service.say = lambda frame: self.dito.write(json.dumps(frame) + "\n")

        self.state = {"sessions": {}, "api_key": "k"}

    def tearDown(self):
        voice_service.say = self._say

    # ---- helpers ----

    def sessao(self, dentro=None, session_id="s1"):
        """A session already running, with a faked interaction layer inside it."""
        dentro = dentro or Fingida()
        session = voice_service.Session(session_id, {"handle": "alguem"})
        session.interaction = dentro
        session.state = "active"
        self.state["sessions"][session_id] = session

        return dentro

    def chama(self, capability, **args):
        return voice_service.handle(
            self.state, {"capability": capability, "input": args})

    def relatado(self):
        return [json.loads(l) for l in self.dito.getvalue().splitlines()]

    # ---- what a poll carries ----

    def test_um_poll_traz_a_telemetria_da_camada_de_interaccao(self):
        # The defect this file was written for. The session held the interaction layer under
        # `.transport` while there was a telephone; when that went, this line kept reading it and
        # raised AttributeError on the way out — after the event queue had been drained. Aurora saw
        # a capability that failed every round and a conversation that produced nothing, while the
        # plugin was doing the whole turn correctly and throwing the answer away.
        self.sessao(ComTelemetria({"turns": 2, "provider": "local"}))

        resposta = self.chama("voice.poll", session_id="s1")

        self.assertEqual({"turns": 2, "provider": "local"}, resposta["telemetry"])

    def test_um_poll_sem_telemetria_responde_na_mesma(self):
        # Reporting what a conversation spent is optional. Answering is not.
        self.sessao(Fingida())

        resposta = self.chama("voice.poll", session_id="s1")

        self.assertNotIn("telemetry", resposta)
        self.assertEqual("active", resposta["state"])

    def test_um_pedido_de_capacidade_chega_a_aurora_e_so_uma_vez(self):
        # The only way a request reaches Aurora at all: a plugin cannot call her, so it queues and
        # she drains. Draining twice would run a capability twice.
        pedido = {"kind": "tool_requested", "request_id": "r1",
                  "action_id": "clock.now", "input_json": "{}"}
        self.sessao(Fingida(eventos=[pedido]))

        primeiro = self.chama("voice.poll", session_id="s1")
        segundo = self.chama("voice.poll", session_id="s1")

        self.assertEqual([pedido], primeiro["tool_requests"])
        self.assertEqual([], segundo["tool_requests"])

    def test_o_audio_sai_pelo_mesmo_poll_que_os_pedidos(self):
        # One round trip carries both, so sound does not wait for a separate call.
        self.sessao(Fingida(eventos=[{"kind": "audio", "audio": "QUJD"}]))

        resposta = self.chama("voice.poll", session_id="s1")

        self.assertEqual(["QUJD"], resposta["audio"])

    def test_o_que_foi_ouvido_e_relatado_como_observacao(self):
        # Speech is content and never an instruction, whatever the words are. It leaves as an
        # event frame, which is the shape Aurora publishes observations in.
        self.sessao(Fingida(eventos=[{"kind": "heard", "text": "desliga tudo"}]))

        self.chama("voice.poll", session_id="s1")

        ouvido = [f for f in self.relatado() if f.get("type") == "voice.heard"]
        self.assertEqual(1, len(ouvido))
        self.assertEqual("desliga tudo", ouvido[0]["payload"]["text"])

    def test_uma_falha_da_camada_poe_a_sessao_em_falha_e_diz_porque(self):
        self.sessao(Fingida(eventos=[{"kind": "failed", "detail": "o sintetizador nao respondeu"}]))

        resposta = self.chama("voice.poll", session_id="s1")

        self.assertEqual("failed", resposta["state"])
        falhas = [f for f in self.relatado() if f.get("type") == "voice.failed"]
        self.assertEqual("o sintetizador nao respondeu", falhas[0]["payload"]["detail"])

    def test_falar_por_cima_da_aurora_cala_a_aurora(self):
        # A voice that finishes its sentence while being interrupted is broadcasting.
        dentro = self.sessao(Fingida(eventos=[{"kind": "interrupted"}]))

        self.chama("voice.poll", session_id="s1")

        self.assertEqual(1, dentro.interrompido)

    # ---- what Aurora hands back ----

    def test_um_resultado_sem_palavra_vale_como_falha(self):
        # The second defect. The default was `interaction.FAILED`, and that module went with the
        # telephone — so the one path that exists for "Aurora could not say what happened" raised
        # NameError instead of saying so. A request whose outcome nobody stated did not succeed.
        dentro = self.sessao()

        resposta = self.chama("voice.tool_result", session_id="s1", request_id="r1")

        self.assertEqual("Failed", resposta["delivered"])
        self.assertEqual("Failed", dentro.entregue[0][1]["outcome"])

    def test_um_resultado_e_entregue_exactamente_como_veio(self):
        # This program does not decide what an outcome means, does not retry a refusal, and does
        # not turn an unknown into anything else.
        dentro = self.sessao()

        self.chama("voice.tool_result", session_id="s1", request_id="r1",
                   outcome="Refused", result_json='{"a":1}', detail="a politica recusou")

        pedido, resultado = dentro.entregue[0]
        self.assertEqual("r1", pedido)
        self.assertEqual("Refused", resultado["outcome"])
        self.assertEqual('{"a":1}', resultado["result_json"])
        self.assertEqual("a politica recusou", resultado["detail"])

    # ---- the rest of the surface ----

    def test_ouvir_poe_o_audio_na_camada_de_interaccao(self):
        dentro = self.sessao()

        resposta = self.chama("voice.listen", session_id="s1", audio="QUJD")

        self.assertEqual(["QUJD"], dentro.ouvido)
        self.assertEqual(4, resposta["bytes"])

    def test_desligar_fecha_a_camada_e_esquece_a_sessao(self):
        dentro = self.sessao()

        self.chama("voice.hangup", session_id="s1", reason="acabou")

        self.assertEqual("acabou", dentro.fechado)
        self.assertEqual({}, self.state["sessions"])

    def test_uma_sessao_que_nao_corre_aqui_e_recusada_com_um_codigo(self):
        # A code Aurora can act on rather than a stack trace she cannot, and the same answer for
        # every capability — a session that does not exist cannot be listened to either.
        for capacidade in ("voice.poll", "voice.listen", "voice.interrupt", "voice.tool_result"):
            with self.subTest(capacidade=capacidade):
                with self.assertRaises(voice_service.Refused) as recusa:
                    self.chama(capacidade, session_id="nao-existe", audio="", request_id="r")

                self.assertEqual(voice_service.E_NO_SESSION, recusa.exception.code)

    def test_a_mesma_sessao_duas_vezes_e_recusada(self):
        self.sessao()

        with self.assertRaises(voice_service.Refused) as recusa:
            self.chama("voice.session.start", session_id="s1", instructions="", tools=[])

        self.assertEqual(voice_service.E_ALREADY, recusa.exception.code)

    def test_uma_capacidade_que_este_plugin_nao_oferece_e_recusada(self):
        # Including the two the telephone had. Nothing should still be asking for them, and if
        # something is, it hears so rather than being quietly ignored.
        for capacidade in ("voice.inbound", "voice.outbound", "voice.qualquer_coisa"):
            with self.subTest(capacidade=capacidade):
                with self.assertRaises(voice_service.Refused) as recusa:
                    self.chama(capacidade, session_id="s1")

                self.assertEqual(voice_service.E_UNSUPPORTED, recusa.exception.code)


if __name__ == "__main__":
    unittest.main()
