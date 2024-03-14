import json
from difflib import get_close_matches as yakin_sonuc_getir

def load_data():
    with open("\\data.json", "r") as dosya:
        return json.load(dosya)# Burada Veritabanını Okuyor.
    
def data_write(veriler):
    with open("\\data.json", "w")as dosya:
        json.dump(veriler, dosya, indent=2)# Burada Verilen Bilgiler Dataya Yazılıyor, indent=2 2 satır Boş Bırak.


def get_close_find(soru,sorular):
    eslesen = yakin_sonuc_getir(soru, sorular, 1, 0.7)
    return eslesen[0] if eslesen else None # Burada Eslesen Soruların Cevabını Getirir.

def find_answer(soru ,data):
    for soru_cevaplar in data["Sorular"]:
        if soru_cevaplar["soru"] == soru:
            return soru_cevaplar["cevap"]
    return None



def chat_bot():
    data = load_data()

    while True:
        soru = input("Siz: ")

        if soru == "çık":
            break

        gelen_sonuc = get_close_find(soru, [soru_cevaplar["soru"] for soru_cevaplar in data["Sorular"]])

        if gelen_sonuc:
            cevap = find_answer(gelen_sonuc, data)
            print(f"Bot: {cevap}")
        else:
            print("Bu Soruyu Bilmiyorum, Öğretirmisin?")
            new_cevap = input("Öğretmek için yazın veya geç yazın: ")

            if new_cevap != "geç":
                data["Sorular"].append({
                    "soru":soru,
                    "cevap":new_cevap
                })
                data_write(data)
                print("Sayende Yeni Birşey Öğrenedim :)")
            else:
                break



if __name__ == '__main__':
    chat_bot()